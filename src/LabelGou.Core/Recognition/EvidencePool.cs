using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>一个值在文本层里找到的证据强度。等级从低到高排。</summary>
public enum EvidenceLevel
{
    /// <summary>整篇文本层里找不到这个值的任何痕迹——大模型最可疑的一种情况。</summary>
    None = 0,

    /// <summary>只有数字能对上（写法对不上）。OCR 常把小数点弄丢，所以这级既不能当"已证实"也不能当"编的"。</summary>
    DigitsOnly = 1,

    /// <summary>去掉空格与标点后能对上。这是本机 OCR 输出下最常见的一级。</summary>
    Compact = 2,

    /// <summary>原文里能整段找到。最硬。</summary>
    Exact = 3,
}

/// <summary>证据命中结果。</summary>
public sealed record EvidenceMatch(EvidenceLevel Level, int LineIndex, string Line)
{
    public static readonly EvidenceMatch NotFound = new(EvidenceLevel.None, -1, string.Empty);

    public bool Found => Level != EvidenceLevel.None;

    public override string ToString() => Level == EvidenceLevel.None ? "无证据" : $"第 {LineIndex + 1} 行：{Line}";
}

/// <summary>
/// 文本层作为「证据池」（定案 D12 的地基）。
/// <para>为什么用 OCR 文本而不是"再问一次模型"来交叉校验：模型会凭空造出一个完全合理的假合同号，
/// 而 OCR 只会把已有的字认错、不会无中生有。所以「这个值能不能在文本层找到痕迹」是有意义的判据，
/// 「两个模型是否同意」不是。</para>
/// </summary>
public sealed class EvidencePool
{
    private readonly List<(string Raw, string Compact)> _entries = new();

    public EvidencePool(RecognizedText? text)
    {
        if (text is null) return;
        foreach (var line in text.Lines)
        {
            var raw = TextNormalizer.Squeeze(line.Text);
            if (raw.Length == 0) continue;
            _entries.Add((raw, TextNormalizer.Compact(raw)));
        }
    }

    /// <summary>池子里有多少可用行。</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// 找一个值的证据。取最强的一级；同级取最靠前的行（唛头通常自上而下读）。
    /// <para>短值（压缩后不足 3 字符）另设一闸：<strong>必须整词命中</strong>才算原文证据。
    /// 本机真样本里 “件号 3” 曾在 “MM 2603” 里被当成原文命中，给用户指一条毫不相干的证据行，
    /// 核对时反而误导。</para>
    /// </summary>
    public EvidenceMatch Find(string? value)
    {
        var probe = TextNormalizer.Squeeze(value);
        if (probe.Length == 0 || _entries.Count == 0) return EvidenceMatch.NotFound;

        var compactProbe = TextNormalizer.Compact(probe);
        var shortProbe = compactProbe.Length < 3;
        var best = EvidenceMatch.NotFound;
        EvidenceMatch? loose = null;

        for (var i = 0; i < _entries.Count; i++)
        {
            var (raw, compact) = _entries[i];
            var level = EvidenceLevel.None;

            if (raw.Contains(probe, StringComparison.OrdinalIgnoreCase)) level = EvidenceLevel.Exact;
            else if (compactProbe.Length > 0 && compact.Contains(compactProbe)) level = EvidenceLevel.Compact;
            else if (TextNormalizer.DigitsCovered(probe, raw) && ContainsAnyDigit(probe)) level = EvidenceLevel.DigitsOnly;

            if (level == EvidenceLevel.None) continue;

            if (shortProbe && level != EvidenceLevel.DigitsOnly
                && !(level == EvidenceLevel.Exact ? BoundaryHit(raw, probe) : BoundaryHit(compact, compactProbe)))
            {
                // 只在“真的含数字”时留个弱兜底，否则短字母碎片处处“命中”更说不清
                if (loose is null && ContainsAnyDigit(probe))
                    loose = new EvidenceMatch(EvidenceLevel.DigitsOnly, i, raw);
                continue;
            }

            if (level <= best.Level) continue;
            best = new EvidenceMatch(level, i, raw);
            if (level == EvidenceLevel.Exact) break;
        }

        return best.Level == EvidenceLevel.None && loose is not null ? loose : best;
    }

    /// <summary>整词命中：探针在文本里出现，且两侧不是字母数字。</summary>
    private static bool BoundaryHit(string text, string probe)
    {
        var at = text.IndexOf(probe, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            var end = at + probe.Length;
            var beforeOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var afterOk = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (beforeOk && afterOk) return true;
            at = text.IndexOf(probe, end, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// 值的一部分能否在文本层里找到——用于「两通道不一致」时判断该信谁。
    /// <para>按连续的字母数字段逐段试（≥ 3 字符），命中任一段就算有支撑。刻意写成线性的：
    /// 扫全部子串是把 O(n²) 埋进印刷链路的典型自伤。</para>
    /// </summary>
    public bool PartiallySupported(string? value)
    {
        if (_entries.Count == 0) return false;

        foreach (var chunk in AlphaRuns(value))
        {
            if (chunk.Length < 3) continue;
            foreach (var (_, compact) in _entries)
            {
                if (compact.Contains(chunk, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }

    /// <summary>拆出连续的字母数字段（已压缩：小写、去标点与空格）。</summary>
    private static IEnumerable<string> AlphaRuns(string? value)
    {
        var runs = new List<string>();
        if (string.IsNullOrEmpty(value)) return runs;

        var current = new System.Text.StringBuilder();
        foreach (var ch in TextNormalizer.ToHalfwidth(value) + " ")
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (current.Length > 0)
            {
                runs.Add(current.ToString());
                current.Clear();
            }
        }

        return runs;
    }

    private static bool ContainsAnyDigit(string text) => text.Any(char.IsAsciiDigit);
}
