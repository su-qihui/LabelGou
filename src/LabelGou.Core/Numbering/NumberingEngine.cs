using System.Globalization;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Numbering;

/// <summary>
/// 一次编号的产出。
/// </summary>
public sealed class NumberingResult
{
    public NumberingResult(
        IReadOnlyList<MarkRecord> labels,
        IReadOnlyList<TemplateIssue> issues,
        int sourceRecordCount,
        int cartonCount,
        int groupCount,
        bool ruleRejected)
    {
        Labels = labels;
        Issues = issues;
        SourceRecordCount = sourceRecordCount;
        CartonCount = cartonCount;
        GroupCount = groupCount;
        RuleRejected = ruleRejected;
    }

    /// <summary>要出片的标签记录集（一箱一张，已按 <see cref="NumberingRule.Copies"/> 复制）。</summary>
    public IReadOnlyList<MarkRecord> Labels { get; }

    /// <summary>规则问题（含"规则被拒绝"的 Error）。</summary>
    public IReadOnlyList<TemplateIssue> Issues { get; }

    /// <summary>进来的记录数（Excel 行数）。</summary>
    public int SourceRecordCount { get; }

    /// <summary>展开后的<strong>箱数</strong>：件号 y 就是按它算的，与每张份数无关。</summary>
    public int CartonCount { get; }

    /// <summary>分组数（全局口径恒为 1）。</summary>
    public int GroupCount { get; }

    /// <summary>true 表示规则本身不合法，<see cref="Labels"/> 原样退回、未做任何改写。</summary>
    public bool RuleRejected { get; }

    public int LabelCount => Labels.Count;

    /// <summary>给人看的一句话，例如"9 行 → 展开 120 箱 → 共 240 张标签"。</summary>
    public string Describe()
    {
        if (RuleRejected) return "编号规则不合法，已按原始数据出标签";
        var expanded = CartonCount != SourceRecordCount ? $" → 展开 {CartonCount} 箱" : string.Empty;
        var copies = LabelCount != CartonCount ? $" → 共 {LabelCount} 张标签" : string.Empty;
        var groups = GroupCount > 1 ? $"（{GroupCount} 组）" : string.Empty;
        return $"{SourceRecordCount} 行{expanded}{copies}{groups}";
    }
}

/// <summary>
/// 编号引擎：把「记录集 + 规则」变成「一箱一张、件号已定」的标签记录集。
/// <para>
/// 三条不可动摇的规矩（都有单测锁死）：
/// ① <strong>不臆造</strong>——只有规则明确要求重排时才覆盖数据里已有的件号，覆盖条数会作为提示回给界面；
/// ② <strong>口径唯一</strong>——件号 y 是「组内总箱数」，与一份几张（<see cref="NumberingRule.Copies"/>）无关，
///    否则左右各贴一张就会印成 "3 / 240" 这种吓人数字；
/// ③ <strong>可回溯</strong>——推算值一律 <see cref="ValueOrigin.Rule"/>，且 SourceRef 上标明是编号引擎给的。
/// </para>
/// </summary>
public static class NumberingEngine
{
    public static NumberingResult Apply(IReadOnlyList<MarkRecord> records, NumberingRule rule)
    {
        ArgumentNullException.ThrowIfNull(records);
        rule ??= NumberingRule.Default();

        var issues = new List<TemplateIssue>(rule.Validate());
        if (issues.HasError())
        {
            // 规则不合法就不动数据：宁可让操作员看到原始件号，也不印一套推演出来的错号
            return new NumberingResult(records, issues, records.Count, records.Count, 1, ruleRejected: true);
        }

        // —— 1. 每行展开成几箱 ——
        var cartonsPerRecord = new int[records.Count];
        var expand = rule.Mode == NumberingMode.ExpandByCartonTotal;
        for (var i = 0; i < records.Count; i++)
        {
            if (!expand)
            {
                cartonsPerRecord[i] = 1;
                continue;
            }
            var (count, note) = ReadCartonCount(records[i], rule.ExpandCountField);
            if (note is not null)
                issues.Add(new TemplateIssue(IssueLevel.Warning, $"第 {i + 1} 行：{note}"));
            cartonsPerRecord[i] = count;
        }

        var totalCartons = 0L;
        foreach (var c in cartonsPerRecord) totalCartons += c;
        var totalLabels = totalCartons * rule.Copies;
        if (totalLabels > NumberingRule.MaxTotalLabels)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error,
                $"按此规则将生成 {totalLabels.ToString("N0", CultureInfo.InvariantCulture)} 张标签，超过上限 " +
                $"{NumberingRule.MaxTotalLabels.ToString("N0", CultureInfo.InvariantCulture)} 张，请检查「总件数」列是否填错。"));
            return new NumberingResult(records, issues, records.Count, (int)Math.Min(totalCartons, int.MaxValue), 1, ruleRejected: true);
        }

        // —— 2. 分组（决定件号从哪开始重置、y 按哪个总数算） ——
        var groups = BuildGroups(records, rule, cartonsPerRecord);

        // —— 3. 逐组连续编号，产出标签记录 ——
        var labels = new List<MarkRecord>((int)totalLabels);
        var overwritten = 0;
        foreach (var group in groups)
        {
            var groupCartons = 0;
            foreach (var entry in group) groupCartons += entry.Cartons;

            var counter = (long)rule.Start;
            var seq = 0;

            foreach (var entry in group)
            {
                var source = records[entry.RecordIndex];
                for (var carton = 0; carton < entry.Cartons; carton++)
                {
                    var numberText = rule.FormatNumber(counter);
                    var totalText = rule.FormatTotal(groupCartons);
                    seq++;

                    for (var copy = 0; copy < Math.Max(1, rule.Copies); copy++)
                    {
                        labels.Add(BuildRecord(source, rule, numberText, totalText, expand, carton, entry.Cartons, seq));
                    }

                    counter += Math.Max(1, rule.Step);
                    if (rule.Mode != NumberingMode.KeepData && entry.HasDataNumber) overwritten++;
                }
            }
        }

        if (overwritten > 0)
        {
            issues.Add(new TemplateIssue(IssueLevel.Info,
                $"已按规则重排 {overwritten} 条原本带件号的记录（如需保留数据原值，请改用「沿用数据件号」）。"));
        }

        return new NumberingResult(
            labels,
            issues,
            records.Count,
            clampInt(totalCartons),
            groups.Count,
            ruleRejected: false);
    }

    /// <summary>
    /// 单个标签应使用的件号/总件数文本如何取舍。
    /// </summary>
    private static MarkRecord BuildRecord(
        MarkRecord source,
        NumberingRule rule,
        string numberText,
        string totalText,
        bool expand,
        int cartonOffset,
        int cartonsOfRow,
        int sequenceInGroup)
    {
        var builder = source.ToBuilder();
        var sourceRef = (source.SourceRef is null ? string.Empty : source.SourceRef + " · ") +
                        $"编号规则「{rule.Name}」" +
                        (expand ? $"（第 {cartonOffset + 1}/{cartonsOfRow} 箱）" : string.Empty);
        builder.SetRow(source.SourceRowIndex, sourceRef);

        switch (rule.Mode)
        {
            case NumberingMode.KeepData:
                // 只在数据缺失时补号，补出来的也标 Rule，与导入值区分
                if (!source.Has(MarkFieldKey.CartonNo))
                    builder.Set(MarkFieldKey.CartonNo, new MarkValue(numberText, ValueOrigin.Rule) { SourceRef = sourceRef });
                if (!source.Has(MarkFieldKey.CartonTotal))
                    builder.Set(MarkFieldKey.CartonTotal, new MarkValue(totalText, ValueOrigin.Rule) { SourceRef = sourceRef });
                break;

            case NumberingMode.ForceSequence:
            case NumberingMode.ExpandByCartonTotal:
            default:
                builder.Set(MarkFieldKey.CartonNo, new MarkValue(numberText, ValueOrigin.Rule) { SourceRef = sourceRef });
                builder.Set(MarkFieldKey.CartonTotal, new MarkValue(totalText, ValueOrigin.Rule) { SourceRef = sourceRef });
                break;
        }

        // 组内序号作为可引用的自定义量，模板可用 {{col:组内序}} 排查错号
        builder.SetCustom("col:组内序", sequenceInGroup.ToString(CultureInfo.InvariantCulture), ValueOrigin.Rule);

        // 「本行几箱」必须单独留一个量：展开模式下 {{CartonTotal}} 已被改写成整批总数（15），
        // 而厂商唛头印的是本货号自己的箱数（真样张：Ctns：5件）。不留这个量，5 就会被悄悄印成 15。
        var rowCartonText = expand
            ? cartonsOfRow.ToString(CultureInfo.InvariantCulture)
            : source.GetText(rule.ExpandCountField) ?? cartonsOfRow.ToString(CultureInfo.InvariantCulture);
        builder.SetCustom("col:本行箱数", rowCartonText, ValueOrigin.Rule);

        return builder.Build();
    }

    /// <summary>读一行的箱数；拿不到合法正整数时按 1 箱处理并回一句原因。</summary>
    private static (int Cartons, string? Note) ReadCartonCount(MarkRecord record, MarkFieldKey field)
    {
        var text = record.GetText(field);
        if (string.IsNullOrWhiteSpace(text))
            return (1, $"「{DefName(field)}」为空，按 1 箱处理");

        // 允许 "12"、"12.0"、"12 箱"、"12ctn" 这类写法，取第一个整数
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            return (1, $"「{DefName(field)}」＝「{text}」读不出整数，按 1 箱处理");

        if (count <= 0) return (1, $"「{DefName(field)}」＝0，按 1 箱处理");
        if (count > NumberingRule.MaxExpandPerRecord)
            return (NumberingRule.MaxExpandPerRecord,
                $"「{DefName(field)}」＝{count.ToString(CultureInfo.InvariantCulture)} 超过单行上限 {NumberingRule.MaxExpandPerRecord}，已截断");
        return (count, null);
    }

    private static string DefName(MarkFieldKey field)
        => MarkFieldCatalog.TryGet(field, out var def) ? def.ChineseName : field.ToString();

    private static int clampInt(long value) => value > int.MaxValue ? int.MaxValue : (int)value;

    /// <summary>组内的一项：哪一行、展开几箱、数据里是否本来就有件号。</summary>
    private readonly record struct GroupEntry(int RecordIndex, int Cartons, bool HasDataNumber);

    private static List<List<GroupEntry>> BuildGroups(
        IReadOnlyList<MarkRecord> records,
        NumberingRule rule,
        int[] cartonsPerRecord)
    {
        var entries = new List<GroupEntry>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            entries.Add(new GroupEntry(i, cartonsPerRecord[i], records[i].Has(MarkFieldKey.CartonNo)));
        }

        if (rule.Scope != NumberingScope.PerGroup || rule.GroupByField is null)
        {
            return new List<List<GroupEntry>> { entries };
        }

        var field = rule.GroupByField.Value;
        var order = new List<string>();
        var buckets = new Dictionary<string, List<GroupEntry>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < records.Count; i++)
        {
            var key = records[i].GetText(field);
            if (string.IsNullOrWhiteSpace(key)) key = "（空）";
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new List<GroupEntry>();
                buckets[key] = bucket;
                order.Add(key);
            }
            bucket.Add(entries[i]);
        }

        return order.Select(key => buckets[key]).ToList();
    }
}
