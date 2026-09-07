using System.Text;
using System.Text.RegularExpressions;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>规则抽取的产出：候选值 + 过程中的告警。</summary>
public sealed class RuleExtractionResult
{
    public List<FieldCandidate> Candidates { get; } = new();

    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 不依赖任何模型的字段抽取：拿唛头字段别名/标签去锚文本行，锚点后面的那截就是值。
/// <para>这条通道存在的理由不是「比大模型准」，而是<strong>它不会凭空造值</strong>：
/// 每一个候选都能指回一行原文。所以它同时充当 D12 里校核大模型的那一路。</para>
/// </summary>
public static class RuleFieldExtractor
{
    /// <summary>超过这个行数的文档视为异常（扫描件被切成几百条碎行），只处理前这么多行。</summary>
    public const int MaxLines = 400;

    /// <summary>候选值上限，防呆用（异常文本一行能锚出几十个假候选）。</summary>
    public const int MaxCandidates = 80;

    private sealed record Alias(string Compact, MarkFieldKey Field, int Rank);

    private static readonly List<Alias> Aliases = BuildAliases();

    private static readonly Regex CartonPair = new(
        @"(?i)\b(?:no|ctn|box)\.?\s*[:]?\s*(\d{1,5})\s*[/／]\s*(\d{1,5})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MadeIn = new(
        @"(?i)\bmade\s*in\s+([A-Za-z\u4e00-\u9fff][A-Za-z\u4e00-\u9fff .,-]{1,28}[A-Za-z\u4e00-\u9fff])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SizeTriple = new(
        @"(?i)\b(\d{1,4}(?:\.\d+)?)\s*[x×]\s*(\d{1,4}(?:\.\d+)?)\s*[x×]\s*(\d{1,4}(?:\.\d+)?)\s*(cm|mm)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CbmValue = new(
        @"(?i)\b(\d{1,4}(?:\.\d+)?)\s*(cbm|立方米|方)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 别名表：字段英文名、中文名与全部别名一起进表，按压缩形（去空格、去标点、小写）匹配。
    /// <para>用压缩形才扛得住本机 OCR 的实际输出：<c>"N .W."</c> 与 <c>N.W.</c> 会归到同一个别名。
    /// 太短的别名（压缩后不足 2 字符）直接不进表，否则 "of"、"no" 这类会到处误锚。</para>
    /// </summary>
    private static List<Alias> BuildAliases()
    {
        var list = new List<Alias>();
        foreach (var def in MarkFieldCatalog.Mappable)
        {
            foreach (var raw in new[] { def.EnglishLabel, def.ChineseName }.Concat(def.Aliases))
            {
                var compact = TextNormalizer.Compact(raw);
                if (compact.Length < 2) continue;
                var rank = compact.Length switch
                {
                    >= 8 => 3,
                    >= 5 => 2,
                    >= 3 => 1,
                    _ => 0,
                };
                list.Add(new Alias(compact, def.Key, rank));
            }
        }

        // 长别名优先：同一位置上 "contract no" 必须赢过 "no"。
        list.Sort((a, b) => b.Compact.Length.CompareTo(a.Compact.Length));
        return list;
    }

    public static RuleExtractionResult Extract(RecognizedText? text)
    {
        var result = new RuleExtractionResult();
        if (text is null || text.IsEmpty)
        {
            result.Warnings.Add("没有文本层可抽取（OCR 没跑出文字或文档不含正文）。");
            return result;
        }

        var taken = new Dictionary<MarkFieldKey, FieldCandidate>();
        var origin = OriginOf(text.Channel);
        var lineLimit = Math.Min(text.Lines.Count, MaxLines);
        if (text.Lines.Count > MaxLines)
            result.Warnings.Add($"文本行数 {text.Lines.Count} 超过 {MaxLines}，只处理前 {MaxLines} 行（多半是扫描件切得太碎）。");

        for (var i = 0; i < lineLimit; i++)
        {
            if (taken.Count >= MaxCandidates)
            {
                result.Warnings.Add($"字段候选已达上限 {MaxCandidates}，后面的行不再抽取。");
                break;
            }

            ExtractFromLine(text.Lines[i], i, origin, taken);
        }

        ApplyPatternFallbacks(text, lineLimit, origin, taken);

        foreach (var candidate in taken.Values.OrderBy(c => c.Field))
        {
            if (candidate.Field == MarkFieldKey.Logo) continue;   // 图片字段不从文本抽
            result.Candidates.Add(candidate);
        }

        return result;
    }

    /// <summary>
    /// 非大模型通道一律记 <see cref="ValueOrigin.AiOcr"/>：它的语义是「机器从文本层读出来的」，
    /// 跟 <see cref="ValueOrigin.AiLlm"/> 的「模型可能凭空编」相对，这才是核对时要区分的那件事。
    /// </summary>
    private static ValueOrigin OriginOf(TextChannel channel)
        => channel == TextChannel.Llm ? ValueOrigin.AiLlm : ValueOrigin.AiOcr;

    /// <summary>标签锚定：一行里可以有多个标签（"C/S: MACYS  CONTRACT NO: MMJ-2603"），逐个切段。</summary>
    private static void ExtractFromLine(
        TextLine line,
        int index,
        ValueOrigin origin,
        Dictionary<MarkFieldKey, FieldCandidate> taken)
    {
        var original = TextNormalizer.ToHalfwidth(line.Text);
        var compact = new StringBuilder(original.Length);
        var map = new List<int>(original.Length);
        for (var p = 0; p < original.Length; p++)
        {
            var ch = original[p];
            if (!char.IsLetterOrDigit(ch)) continue;
            compact.Append(char.ToLowerInvariant(ch));
            map.Add(p);
        }

        var hits = FindHits(compact.ToString(), original, map);
        if (hits.Count == 0) return;

        for (var h = 0; h < hits.Count; h++)
        {
            var hit = hits[h];
            var from = SlicePoint(original, map, hit.End);
            var stop = h + 1 < hits.Count ? SlicePoint(original, map, hits[h + 1].Start) : original.Length;
            var (value, truncated) = CleanValue(original[from..stop]);
            if (value.Length == 0) continue;

            var def = MarkFieldCatalog.Get(hit.Field);
            if (def.Numeric)
            {
                if (!value.Any(char.IsAsciiDigit)) continue;   // 数值字段没数字 = 误锚，丢掉

                // 件号/总数/数量这种纯整数字段，值里一旦混进字母就是锚错了标签：
                // 本机真实样本里，"（ 0 NTRACT NO: MM 2603" 的裸 "NO:" 会把合同号吐成件号，
                // 不拦的话唛头会印上「No. 2603」这种看着完全正常的错号。
                if (def.Kind == MarkValueKind.Integer && value.Any(char.IsLetter)) continue;
            }

            var confidence = 0.5 + hit.Rank * 0.12 + (value.Any(char.IsAsciiDigit) ? 0.08 : 0);
            if (TextNormalizer.Compact(def.ChineseName) == hit.Compact) confidence += 0.05;

            var candidate = new FieldCandidate
            {
                Field = hit.Field,
                RawValue = value,
                Origin = origin,
                EvidenceLineIndex = index,
                Evidence = line.Text,
                Confidence = Math.Min(0.9, confidence),
                Note = truncated ? "值偏长，可能把相邻内容一起读进来了，请核对" : null,
            };

            if (taken.TryGetValue(hit.Field, out var existing))
            {
                if (existing.Confidence >= candidate.Confidence) continue;
                candidate.Note = $"同字段在别处还有一处「{existing.RawValue}」，已取置信度更高的这一处，请核对";
            }

            taken[hit.Field] = candidate;
        }
    }

    /// <summary>压缩形下标 → 原文下标；越过末尾表示「到行尾」。</summary>
    private static int SlicePoint(string original, List<int> map, int compactIndex)
        => compactIndex >= map.Count ? original.Length : map[compactIndex];

    private readonly record struct Hit(MarkFieldKey Field, string Compact, int Rank, int Start, int End);

    /// <summary>在压缩形里找全部别名命中，按位置排序并剥掉被更长命中包含的短命中。</summary>
    private static List<Hit> FindHits(string compact, string original, List<int> map)
    {
        var found = new List<Hit>();
        foreach (var alias in Aliases)
        {
            var at = compact.IndexOf(alias.Compact, StringComparison.Ordinal);
            while (at >= 0)
            {
                var end = at + alias.Compact.Length;
                // 边界看「别名最后一个字符的下一个原文字符」：不能拿压缩形下标直接当原文下标用，
                // 中间隔着的标点会被数错位置。
                if (IsBoundary(original, map[at] - 1) && IsBoundary(original, map[end - 1] + 1))
                    found.Add(new Hit(alias.Field, alias.Compact, alias.Rank, at, end));
                at = compact.IndexOf(alias.Compact, end, StringComparison.Ordinal);
            }
        }

        if (found.Count == 0) return found;

        // 同起点保留最长别名；起点更晚但落在已保留区间内的（如 "no" 落在 "contract no" 里）丢掉。
        var kept = new List<Hit>();
        foreach (var hit in found.OrderByDescending(x => x.Compact.Length).ThenBy(x => x.Start))
        {
            if (kept.Any(k => k.Field == hit.Field && hit.Start >= k.Start && hit.End <= k.End)) continue;
            if (kept.Any(k => hit.Start >= k.Start && hit.End <= k.End)) continue;
            kept.Add(hit);
        }

        kept.Sort((a, b) => a.Start.CompareTo(b.Start));
        return kept;
    }

    private static bool IsBoundary(string original, int position)
    {
        if (position < 0 || position >= original.Length) return true;
        return !char.IsLetterOrDigit(original[position]);
    }

    /// <summary>锚定值在印面上的合理长度上限；超了就是多吃了内容，要告警而不是静默收下。</summary>
    private const int MaxValueLength = 40;
    
    private static readonly Regex ColumnGap = new(@"\s{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    
    /// <summary>锚点后的原始值清洗：按列间距断尾、去分隔符、限长。</summary>
    private static (string Value, bool Truncated) CleanValue(string raw)
    {
        // 连续 2+ 空格是可靠断点（同一格子里「值 ␣␣ 下一个标签」的列间距通常 ≥ 2）。
        var gap = ColumnGap.Match(raw);
        var head = gap.Success && gap.Index > 2 ? raw[..gap.Index] : raw;
        var value = TextNormalizer.Squeeze(head).Trim(' ', ':', '=', '-', '/', '·', '*', '#', '（', '(');
        if (value.Length == 0) return (string.Empty, false);
    
        return value.Length > MaxValueLength
            ? (value[..MaxValueLength].TrimEnd(), true)
            : (value, false);
    }

    /// <summary>没有标签可锚时的兜底形状：件号对、MADE IN、三段尺寸、CBM。</summary>
    private static void ApplyPatternFallbacks(
        RecognizedText text,
        int lineLimit,
        ValueOrigin origin,
        Dictionary<MarkFieldKey, FieldCandidate> taken)
    {
        for (var i = 0; i < lineLimit; i++)
        {
            var line = text.Lines[i];
            var scan = TextNormalizer.RepairDecimalSeparators(line.Text);

            if (!taken.ContainsKey(MarkFieldKey.CartonNo) || !taken.ContainsKey(MarkFieldKey.CartonTotal))
            {
                var pair = CartonPair.Match(scan);
                if (pair.Success)
                {
                    // 两个缺哪个补哪个：只缺总件数时不能因为件号已有值就连它一起放过。
                    if (!taken.ContainsKey(MarkFieldKey.CartonNo))
                        taken[MarkFieldKey.CartonNo] = Simple(pair.Groups[1].Value, MarkFieldKey.CartonNo, line, i, origin, 0.8);
                    if (!taken.ContainsKey(MarkFieldKey.CartonTotal))
                        taken[MarkFieldKey.CartonTotal] = Simple(pair.Groups[2].Value, MarkFieldKey.CartonTotal, line, i, origin, 0.8);
                }
            }

            if (!taken.ContainsKey(MarkFieldKey.Origin))
            {
                var made = MadeIn.Match(scan);
                if (made.Success)
                    taken[MarkFieldKey.Origin] = Simple(made.Value, MarkFieldKey.Origin, line, i, origin, 0.8);
            }

            if (!taken.ContainsKey(MarkFieldKey.BoxSize))
            {
                var size = SizeTriple.Match(scan);
                if (size.Success)
                    taken[MarkFieldKey.BoxSize] = Simple(size.Value, MarkFieldKey.BoxSize, line, i, origin, 0.7);
            }

            if (!taken.ContainsKey(MarkFieldKey.Measurement))
            {
                var cbm = CbmValue.Match(scan);
                if (cbm.Success)
                    taken[MarkFieldKey.Measurement] = Simple(cbm.Groups[1].Value, MarkFieldKey.Measurement, line, i, origin, 0.75);
            }
        }
    }

    private static FieldCandidate Simple(
        string value,
        MarkFieldKey field,
        TextLine line,
        int index,
        ValueOrigin origin,
        double confidence) => new()
        {
            Field = field,
            RawValue = value,
            Origin = origin,
            EvidenceLineIndex = index,
            Evidence = line.Text,
            Confidence = confidence,
            Note = "按形状匹配（无字段标签），请核对",
        };
}
