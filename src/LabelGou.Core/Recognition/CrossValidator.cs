using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 交叉校验后的一个字段，也就是核对窗口里的一行。
/// <para><strong>状态只有一个：<see cref="Confirmed"/></strong>。按定案 D13，AI 来的字段一律算「待核」，
/// 直到用户明确确认；改过值会自动退回待核状态，避免「顺手改了个数就算确认了」。</para>
/// </summary>
public sealed class ReviewedField
{
    private string? _value;
    private bool _confirmed;

    public required MarkFieldKey Field { get; init; }

    public string ChineseName => MarkFieldCatalog.Get(Field).ChineseName;

    public string EnglishLabel => MarkFieldCatalog.Get(Field).EnglishLabel;

    /// <summary>印出去的那个值（已规范化）。改它会立刻把本行退回待核。</summary>
    public string? Value
    {
        get => _value;
        set
        {
            if (string.Equals(_value, value, StringComparison.Ordinal)) return;
            _value = value;
            if (_confirmed) EditCount++;
            _confirmed = false;
        }
    }

    /// <summary>两通道各自的读法，核对时并排给人看。</summary>
    public string? TextValue { get; set; }

    public string? LlmValue { get; set; }

    public ValueOrigin Origin { get; set; }

    public double Confidence { get; set; }

    /// <summary>最终值在文本层里的证据强度。</summary>
    public EvidenceLevel Evidence { get; set; }

    public string EvidenceText { get; set; } = string.Empty;

    public int EvidenceLineIndex { get; set; } = -1;

    /// <summary>两通道是否给出同一个值。false 一定标红。</summary>
    public bool Agreed { get; set; } = true;

    public string? Warning { get; set; }

    public string? Note { get; set; }

    /// <summary>用户改过几次（日志与"这行被人工动过"的事实依据）。</summary>
    public int EditCount { get; private set; }

    /// <summary>是否已由用户确认。D13：未确认不得落库、不得进打印队列。</summary>
    public bool Confirmed
    {
        get => _confirmed;
        set
        {
            _confirmed = value;
            if (!value) return;
            if (string.IsNullOrWhiteSpace(_value)) _confirmed = false;   // 空值不算"已确认"
        }
    }

    public bool IsPending => !Confirmed;

    /// <summary>界面显示用：置信度低或双通道不一致 → 标红。</summary>
    public bool IsHot => !Agreed || Confidence < 0.6 || Warning is not null;

    public override string ToString() => $"{ChineseName}={Value ?? "(空)"}（{(Agreed ? "两通道一致" : "两通道不一致")}，置信 {Confidence:F2}）";
}

/// <summary>
/// 双通道合并与判置信（定案 D12、D13）。
/// <para>三条判据按硬度排序：</para>
/// <list type="number">
/// <item>两通道给出同一个值 → 最可信，但仍要用户确认一次（D13 不看人，只看规则）。</item>
/// <item>只有大模型给了 → 先去文本层找证据。找到就中置信；只找得到数字就明说「小数点存疑」；
/// 完全找不到就写「可能模型编的」并把置信度压到 0.3 以下。这是挡 §五-9 幻觉的那道闸。</item>
/// <item>不一致 → 不静默采信任何一边：证据更强的一方填进结果，但一定标红说明另一方的读法。</item>
/// </list>
/// </summary>
public static class CrossValidator
{
    public static IReadOnlyList<ReviewedField> Merge(
        RecognizedText? text,
        IReadOnlyList<FieldCandidate>? textChannel,
        IReadOnlyList<FieldCandidate>? llmChannel)
    {
        var pool = new EvidencePool(text);
        var fromText = BestOf(textChannel);
        var fromLlm = BestOf(llmChannel);

        var fields = fromText.Keys.Union(fromLlm.Keys).OrderBy(k => (int)k).ToList();
        var result = new List<ReviewedField>(fields.Count);

        foreach (var field in fields)
        {
            fromText.TryGetValue(field, out var a);
            fromLlm.TryGetValue(field, out var b);
            result.Add(Build(field, a, b, pool));
        }

        CheckWeightPair(result);
        return result;
    }

    /// <summary>同一字段多个候选时取置信度最高的那个（同分取先出现的，保持顺序可复现）。</summary>
    private static Dictionary<MarkFieldKey, FieldCandidate> BestOf(IReadOnlyList<FieldCandidate>? candidates)
    {
        var best = new Dictionary<MarkFieldKey, FieldCandidate>();
        if (candidates is null) return best;

        foreach (var candidate in candidates)
        {
            if (best.TryGetValue(candidate.Field, out var existing) && existing.Confidence >= candidate.Confidence) continue;
            best[candidate.Field] = candidate;
        }

        return best;
    }

    private static ReviewedField Build(
        MarkFieldKey field,
        FieldCandidate? a,
        FieldCandidate? b,
        EvidencePool pool)
    {
        var normA = a is null ? null : FieldNormalizer.Normalize(field, a.RawValue);
        var normB = b is null ? null : FieldNormalizer.Normalize(field, b.RawValue);
        var warnings = new List<string>();

        // 只有模型给了值：完全靠证据分级决定可信度。
        if (a is null && b is not null)
        {
            var valueB = normB!.Value;
            var evidenceB = pool.Find(valueB);
            var (confidenceB, noteB) = evidenceB.Level switch
            {
                EvidenceLevel.Exact or EvidenceLevel.Compact => (0.72, "规则通道没锚到字段标签，但模型给的值在文本层里找到了原文。"),
                EvidenceLevel.DigitsOnly => (0.5, "只有数字对得上，写法对不上（OCR 常丢小数点），请逐位核对。"),
                _ => (0.28, "文本层里找不到任何证据，可能是模型编出来的——这一项必须人工看原图。"),
            };

            warnings.Add(noteB);
            if (normB.Warning is { } wB) warnings.Add(wB);

            return new ReviewedField
            {
                Field = field,
                Value = valueB,
                LlmValue = b.RawValue,
                Origin = b.Origin,
                Confidence = confidenceB,
                Evidence = evidenceB.Level,
                EvidenceText = evidenceB.Line,
                EvidenceLineIndex = evidenceB.LineIndex,
                Agreed = false,
                Warning = string.Join(" ", warnings),
                Note = b.Note,
            };
        }

        // 两路都有：先比规范化后的值，再按证据强弱决定填哪个。
        // Agreed 的语义是「两路真的对上了」：只有一路说话时必须为 false。
        // 否则单通道行在核对窗口里显示「一致」且不标红 —— §五-54④ 只拦住了 BulkConfirm，界面仍在骗人（批次一-9）。
        var singleChannel = b is null;
        var compareA = FieldNormalizer.CompareForm(field, a!.RawValue);
        var compareB = b is null ? string.Empty : FieldNormalizer.CompareForm(field, b.RawValue);
        var agreed = !singleChannel && compareA.Length > 0 && string.Equals(compareA, compareB, StringComparison.Ordinal);

        var evidenceA = pool.Find(normA!.Value);
        var evidenceB2 = b is null ? EvidenceMatch.NotFound : pool.Find(normB!.Value);
        var pickText = b is null
            || agreed
            || evidenceA.Level >= evidenceB2.Level
            || a.Confidence >= (b?.Confidence ?? 0);

        var picked = pickText ? a : b!;
        var pickedNorm = pickText ? normA : normB!;
        var pickedEvidence = pickText ? evidenceA : evidenceB2;

        if (!agreed && !singleChannel)
        {
            warnings.Add($"两通道不一致：文本层读作「{normA.Value}」，模型给「{normB!.Value}」，已填「{pickedNorm.Value}」，请对照原图确认。");
        }
        else if (singleChannel)
        {
            // 这句是新的：它把「没人反驳」与「两个都同意」区分开，操作员才会去对原图而不是顺手点确认。
            warnings.Add("只有一路读到这个值（另一路没开口），没有互相印证，请对照原图确认。");
        }

        if (pickedNorm.Warning is { } w) warnings.Add(w);

        var confidence = agreed && b is not null
            ? Math.Min(0.95, Math.Max(a.Confidence, b.Confidence) + 0.1)
            : agreed ? a.Confidence
            : singleChannel ? a.Confidence
            : 0.45;

        return new ReviewedField
        {
            Field = field,
            Value = pickedNorm.Value,
            TextValue = normA.Value,
            LlmValue = b is null ? null : normB!.Value,
            Origin = picked.Origin,
            Confidence = agreed ? confidence : 0.45,
            Evidence = pickedEvidence.Level,
            EvidenceText = pickedEvidence.Line,
            EvidenceLineIndex = pickedEvidence.LineIndex,
            Agreed = agreed,
            Warning = warnings.Count == 0 ? null : string.Join(" ", warnings),
            Note = picked.Note,
        };
    }

    /// <summary>
    /// 跨字段硬矛盾：毛重不可能轻于净重。这类校验只有拼版引擎这边做得了，
    /// 任何单通道逐字段看都看不出问题。
    /// </summary>
    private static void CheckWeightPair(List<ReviewedField> fields)
    {
        if (!TryNumber(fields, MarkFieldKey.GrossWeight, out var gross)
            || !TryNumber(fields, MarkFieldKey.NetWeight, out var net))
        {
            return;
        }

        if (gross >= net) return;

        var target = fields.First(f => f.Field == MarkFieldKey.GrossWeight);
        target.Warning = string.Join(" ", new[] { target.Warning, $"毛重（{target.Value}）轻于净重（{fields.First(f => f.Field == MarkFieldKey.NetWeight).Value}），物理上不可能，请核对这两个数是不是填反了。" }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        target.Confidence = Math.Min(target.Confidence, 0.3);
        target.Agreed = false;
    }

    private static bool TryNumber(List<ReviewedField> fields, MarkFieldKey field, out double value)
    {
        value = 0;
        var target = fields.FirstOrDefault(f => f.Field == field);
        if (target is null) return false;

        var number = TextNormalizer.Numbers(target.Value).FirstOrDefault();
        return number is not null
            && double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }
}
