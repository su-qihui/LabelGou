using System.Globalization;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Numbering;

/// <summary>
/// 件号怎么来。
/// <para>
/// 三种模式覆盖打印店实际遇到的三类单子：Excel 已经把件号写全了、Excel 只给了行序、
/// 以及最常见的一行代表好几箱（一行 = 一个 SKU 装 N 箱）需要展开成 N 张标签。
/// </para>
/// </summary>
public enum NumberingMode
{
    /// <summary>完全沿用数据里的件号/总件数，本引擎一个数字都不改。</summary>
    KeepData = 0,

    /// <summary>强制按规则重排：忽略数据里原有的件号，从 <see cref="NumberingRule.Start"/> 起连续编号。</summary>
    ForceSequence = 1,

    /// <summary>
    /// 按每行的「总件数」列把一行展开成多张标签（一箱一张），件号跨行连续递增。
    /// 外贸装箱单最常见的情形：一行写 12 箱，就要出 12 张 No.x / y。
    /// </summary>
    ExpandByCartonTotal = 2,
}

/// <summary>编号与「总件数 y」的口径。</summary>
public enum NumberingScope
{
    /// <summary>全局连续，y = 全部标签数（一个订单整批打乱也无所谓）。</summary>
    Global = 0,

    /// <summary>按 <see cref="NumberingRule.GroupByField"/> 分组：组内从起始号重新开始，y = 组内标签数。</summary>
    PerGroup = 1,
}

/// <summary>
/// 规则化自动编号配置（M2 的真源，界面只改它的属性）。
/// <para>
/// 印刷件号错一位就是真实货损，所以这里的规则必须<strong>可复现、可解释</strong>：
/// 同一条记录 + 同一条规则，任何时候跑出来的 x/y 都一样；引擎不臆造数据，只按规则推算，
/// 推算出来的值一律带 <see cref="ValueOrigin.Rule"/> 标记，界面上能跟导入数据区分开。
/// </para>
/// </summary>
public sealed class NumberingRule
{
    /// <summary>单条记录展开上限（防 Excel 把"总件数"误填成 999999 直接生成一百万张标签）。</summary>
    public const int MaxExpandPerRecord = 5000;

    /// <summary>单次任务标签总数上限，超过即拒绝。</summary>
    public const int MaxTotalLabels = 200000;

    public string Name { get; set; } = "默认编号规则";

    public NumberingMode Mode { get; set; } = NumberingMode.KeepData;

    public NumberingScope Scope { get; set; } = NumberingScope.Global;

    /// <summary>
    /// 分组字段（Scope=<see cref="NumberingScope.PerGroup"/> 时生效）。
    /// 常用合同号/PO 号；null 视为不分组。
    /// </summary>
    public MarkFieldKey? GroupByField { get; set; }

    /// <summary>起始号。</summary>
    public int Start { get; set; } = 1;

    /// <summary>步长（一般 1；隔箱贴牌等场景可能 2）。</summary>
    public int Step { get; set; } = 1;

    /// <summary>补零位数，0 表示不补。例：位数 3 → 007。</summary>
    public int PadDigits { get; set; }

    /// <summary>件号前缀，如 "No."；只作用于本箱号 x。</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>件号后缀，如 "号"；只作用于本箱号 x。</summary>
    public string Suffix { get; set; } = string.Empty;

    /// <summary>每张标签印几份（一箱左右各贴一张时填 2，件号仍只占一个号）。</summary>
    public int Copies { get; set; } = 1;

    /// <summary>
    /// <see cref="NumberingMode.ExpandByCartonTotal"/> 时，一行有几箱读哪个字段。
    /// 默认读「总件数」，也支持绑到「箱数」类的自定义列由界面改写。
    /// </summary>
    public MarkFieldKey ExpandCountField { get; set; } = MarkFieldKey.CartonTotal;

    /// <summary>
    /// 展开时改读<strong>表里某一列的列名</strong>（非空就优先于 <see cref="ExpandCountField"/>）。
    /// <para>用户 2026-09-08 的原话：「张数一般表格里会有一列写的（例：表格写打印 5 张，那对应的那张就要排出
    /// 5 张整张纸）」。那些列叫「打印张数」/「张数」/「一开四」，而 <see cref="MarkFieldKey"/> 是封闭的 19 个枚举，
    /// 光靠 <see cref="ExpandCountField"/> 根本选不到 —— 所以开一个按列名取数的口子（§十-A-25 的正解）。</para>
    /// </summary>
    public string? ExpandCountColumn { get; set; }

    /// <summary>不分组时的固定写法，省得界面再点一遍。</summary>
    public static NumberingRule Default() => new();

    public NumberingRule Clone() => new()
    {
        Name = Name,
        Mode = Mode,
        Scope = Scope,
        GroupByField = GroupByField,
        Start = Start,
        Step = Step,
        PadDigits = PadDigits,
        Prefix = Prefix,
        Suffix = Suffix,
        Copies = Copies,
        ExpandCountField = ExpandCountField,
        ExpandCountColumn = ExpandCountColumn,
    };

    /// <summary>给人看的一句话，界面状态栏直接可用。</summary>
    public string Describe()
    {
        var mode = Mode switch
        {
            NumberingMode.KeepData => "沿用数据件号",
            NumberingMode.ForceSequence => "强制重排",
            NumberingMode.ExpandByCartonTotal => string.IsNullOrWhiteSpace(ExpandCountColumn)
                ? "按箱数展开"
                : $"按「{Marks.ColumnLabel.SingleLine(ExpandCountColumn)}」列展开",
            _ => Mode.ToString(),
        };
        var scope = Scope == NumberingScope.Global
            ? "全局连续"
            : $"按{GroupDefName()}分组";
        var pad = PadDigits > 0 ? $"补 {PadDigits} 位" : "不补零";
        var copies = Copies > 1 ? $"每张 {Copies} 份" : null;
        return string.Join(" · ", new[] { mode, scope, $"起始 {Start} 步长 {Step}", pad, copies }
            .Where(s => !string.IsNullOrEmpty(s)));
    }

    private string GroupDefName()
        => GroupByField is null || !MarkFieldCatalog.TryGet(GroupByField.Value, out var def)
            ? "字段"
            : def.ChineseName;

    /// <summary>
    /// 规则自检：把不可能或危险的值提前拦住（负步长、补零 20 位、单行展开上万箱等）。
    /// 问题类型沿用 <see cref="TemplateIssue"/>，与模板校验共用同一套闸门语义。
    /// </summary>
    public IReadOnlyList<TemplateIssue> Validate()
    {
        var issues = new List<TemplateIssue>();

        // 起始号/步长/补零在“沿用数据件号”模式下同样有用：数据缺件号时就是拿它们补的，
        // 所以不能按模式跳过校验（否则缺项会被补成相间的同一个号或负数）。
        if (Start < 0)
            issues.Add(new TemplateIssue(IssueLevel.Error, "起始号不能为负。"));
        if (Step <= 0)
            issues.Add(new TemplateIssue(IssueLevel.Error, "步长必须大于 0，否则件号会全部一样。"));
        if (PadDigits is < 0 or > 12)
            issues.Add(new TemplateIssue(IssueLevel.Error, "补零位数只能在 0~12 之间。"));

        if (Copies < 1 || Copies > 99)
            issues.Add(new TemplateIssue(IssueLevel.Error, "每张份数必须在 1~99 之间。"));

        if (Scope == NumberingScope.PerGroup && GroupByField is null)
            issues.Add(new TemplateIssue(IssueLevel.Warning, "选了按分组重置，但没选分组字段，会退化成全局连续。"));

        if (Mode != NumberingMode.KeepData && (Prefix.Length > 12 || Suffix.Length > 12))
            issues.Add(new TemplateIssue(IssueLevel.Warning, "件号前后缀偏长，标签上的件号栏可能放不下。"));

        return issues;
    }

    /// <summary>把件号数值格式化成要印的文本（含补零与前后缀）。</summary>
    public string FormatNumber(long value)
    {
        var digits = PadDigits > 0 ? Math.Max(PadDigits, value.ToString(CultureInfo.InvariantCulture).Length) : 0;
        var body = digits > 0
            ? value.ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
        return Prefix + body + Suffix;
    }

    /// <summary>总件数 y 的写法：只补零，不加前后缀（"No. 3 / 120" 里的 120 不该带 No.）。</summary>
    public string FormatTotal(long total)
    {
        var digits = PadDigits > 0 ? Math.Max(PadDigits, total.ToString(CultureInfo.InvariantCulture).Length) : 0;
        return digits > 0
            ? total.ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : total.ToString(CultureInfo.InvariantCulture);
    }
}
