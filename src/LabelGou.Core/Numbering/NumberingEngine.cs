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
            // （退回的那批仍要补内置计算量，否则模板里引用 {{col:本行箱数}} 的那一行会整条无声消失）
            var untouched = WithComputedQuantities(records, rule, expand: false, cartonsPerRecord: null);
            return new NumberingResult(untouched, issues, records.Count, records.Count, 1, ruleRejected: true);
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
            var clipped = WithComputedQuantities(records, rule, expand, cartonsPerRecord);
            return new NumberingResult(clipped, issues, records.Count, (int)Math.Min(totalCartons, int.MaxValue), 1, ruleRejected: true);
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
    /// 给「没走编号」的那批记录补上内置计算量（<c>col:组内序</c> / <c>col:本行箱数</c>），
    /// 只写推算量、<strong>不改件号与总件数</strong>。
    /// <para>为什么要单独做：规则不合法或超上限时引擎原样退回记录，而这些记录没经过
    /// <see cref="BuildRecord"/>，模板里 <c>Ctns：{{col:本行箱数}}件</c> 会命中「变量全空整条隐藏」
    /// 而无声少印一行（第 9 棒批次一-11）。补了它，那一行才能照常印出（或照常报缺值）。</para>
    /// </summary>
    private static IReadOnlyList<MarkRecord> WithComputedQuantities(
        IReadOnlyList<MarkRecord> records,
        NumberingRule rule,
        bool expand,
        int[]? cartonsPerRecord)
    {
        var list = new List<MarkRecord>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            var source = records[i];
            var builder = source.ToBuilder();
            builder.SetCustom("col:组内序", (i + 1).ToString(CultureInfo.InvariantCulture), ValueOrigin.Rule);
            var rowCarton = expand && cartonsPerRecord is not null
                ? cartonsPerRecord[i].ToString(CultureInfo.InvariantCulture)
                : RowDataText(source, rule.ExpandCountField);
            if (!string.IsNullOrWhiteSpace(rowCarton))
                builder.SetCustom("col:本行箱数", rowCarton!, ValueOrigin.Rule);
            list.Add(builder.Build());
        }
        return list;
    }

    /// <summary>
    /// 取一个字段「属于这一行本身」的文本：表格列、识别通道认出的值、逐行手填值。
    /// <para>两类值刻意不算数据：<see cref="ValueOrigin.Rule"/>（工具自己按行序兜底填的号）与
    /// <see cref="ValueOrigin.BatchFixed"/>（整批共用的常量）。前者会把九行表撑成 9×9=81 张，
    /// 后者会把「整批共 155 件」当成每行的箱数（§五-62 同一类错，第 9 棒批次一-1/2）。</para>
    /// </summary>
    private static string? RowDataText(MarkRecord record, MarkFieldKey field)
    {
        var value = record.Get(field);
        if (value is null) return null;
        return value.Origin is ValueOrigin.Rule or ValueOrigin.BatchFixed ? null : value.Text;
    }

    /// <summary>
    /// 件号到底算不算「数据里本来就有」：只有工具兜底填的 <see cref="ValueOrigin.Rule"/> 不算。
    /// <para>否则 <c>RecordMapper.Map</c> 先把件号填成行序，<c>KeepData</c> 分支见「已有值」就不动，
    /// 用户设的起始号/补零/前后缀会整体失效（§五-20 那条无条件校验白做，批次一-3）。</para>
    /// </summary>
    private static bool HasRowNumber(MarkRecord record, MarkFieldKey field)
        => record.Get(field) is { } v
           && !string.IsNullOrWhiteSpace(v.Text)
           && v.Origin != ValueOrigin.Rule;

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
                // 只在数据缺失时补号，补出来的也标 Rule，与导入值区分。
                // 「缺失」的判据是「不是表里的值」而不是「没值」：RecordMapper.Map 已按行序兜底填过一轮，
                // 拿 Has() 当判据会让用户设的起始号/补零/前后缀整体失效（批次一-3）。
                if (!HasRowNumber(source, MarkFieldKey.CartonNo))
                    builder.Set(MarkFieldKey.CartonNo, new MarkValue(numberText, ValueOrigin.Rule) { SourceRef = sourceRef });
                if (!HasRowNumber(source, MarkFieldKey.CartonTotal))
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
        // 不展开时也只能拿表里真正的值：CartonTotal 可能是 Map 兜底填的整批行数（批次一-2），
        // 拿它当「本行箱数」就是把 15 件印在每箱上；没值就留给界面报缺，不臆造。
        var rowCartonText = expand
            ? cartonsOfRow.ToString(CultureInfo.InvariantCulture)
            : RowDataText(source, rule.ExpandCountField);
        if (!string.IsNullOrWhiteSpace(rowCartonText))
            builder.SetCustom("col:本行箱数", rowCartonText!, ValueOrigin.Rule);

        return builder.Build();
    }

    /// <summary>
    /// 读一行的箱数；拿不到合法正整数时按 1 箱处理并回一句原因。
    /// <para>只认属于本行的数据（<see cref="RowDataText"/>）：兜底填进 <c>CartonTotal</c> 的
    /// 整批行数或被当成「本行几箱」，九行表就会变成 9×9=81 张标签。</para>
    /// </summary>
    private static (int Cartons, string? Note) ReadCartonCount(MarkRecord record, MarkFieldKey field)
    {
        var text = RowDataText(record, field);
        if (string.IsNullOrWhiteSpace(text))
            return (1, $"「{DefName(field)}」不是表格里的值（没连到列或被兜底填成整批总数），本行按 1 箱处理");

        // 允许 "12"、"12 箱"、"12ctn" 这类写法：取第一个连续数字串。
        // 不能像以前那样把所有数字字符拼起来——注释自证的 "12.0" 会被拼成 120（十倍箱数，批次一-1）。
        var match = FirstIntegerPattern.Match(text);
        if (!match.Success || !int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            return (1, $"「{DefName(field)}」＝「{text}」读不出整数，按 1 箱处理");

        if (count <= 0) return (1, $"「{DefName(field)}」＝0，按 1 箱处理");
        if (count > NumberingRule.MaxExpandPerRecord)
            return (NumberingRule.MaxExpandPerRecord,
                $"「{DefName(field)}」＝{count.ToString(CultureInfo.InvariantCulture)} 超过单行上限 {NumberingRule.MaxExpandPerRecord}，已截断");
        return (count, null);
    }

    private static string DefName(MarkFieldKey field)
        => MarkFieldCatalog.TryGet(field, out var def) ? def.ChineseName : field.ToString();

    /// <summary>取第一个连续数字串（不是把所有数字拼起来）。</summary>
    private static readonly System.Text.RegularExpressions.Regex FirstIntegerPattern = new(
        @"\d+",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
