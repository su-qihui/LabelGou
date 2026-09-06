namespace LabelGou.Core.Marks;

/// <summary>
/// 唛头标准字段键。
/// <para>
/// 这是整个项目的<strong>字段枚举白名单</strong>：M1 由 Excel/CSV 填充，M6 由 AI Agent 识别填充，
/// 两者共用同一套键；M7 的「AI 生成模板」也只能引用这里的键（越界即校验失败）。
/// 新增字段只需在 <see cref="MarkFieldCatalog"/> 登记一条，映射层与模板层自动可用。
/// </para>
/// <para>
/// ⚠️ 不要随意重命名已有序号对应的枚举成员名——它会出现在用户保存的映射方案与模板 JSON 里。
/// </para>
/// </summary>
public enum MarkFieldKey
{
    /// <summary>收货人 / 客户简称（唛头顶部的 "致 / To"）。</summary>
    Consignee = 1,

    /// <summary>客户代码。</summary>
    ClientCode = 2,

    /// <summary>合同号 / 订单号。</summary>
    ContractNo = 3,

    /// <summary>PO 号。</summary>
    PoNumber = 4,

    /// <summary>货号 / 款号 / SKU。</summary>
    ItemNo = 5,

    /// <summary>目的港。</summary>
    DestinationPort = 6,

    /// <summary>目的国。</summary>
    DestinationCountry = 7,

    /// <summary>件号（当前箱序号 x，配合 <see cref="CartonTotal"/> 渲染成 “No. x / y”）。</summary>
    CartonNo = 8,

    /// <summary>总件数 y。</summary>
    CartonTotal = 9,

    /// <summary>每箱数量 / PCS。</summary>
    Quantity = 10,

    /// <summary>毛重 G.W.。</summary>
    GrossWeight = 11,

    /// <summary>净重 N.W.。</summary>
    NetWeight = 12,

    /// <summary>体积 Meas. / CBM。</summary>
    Measurement = 13,

    /// <summary>外箱尺寸 L×W×H。</summary>
    BoxSize = 14,

    /// <summary>批次号。</summary>
    BatchNo = 15,

    /// <summary>出货日期。</summary>
    ShipDate = 16,

    /// <summary>原产地（Made in China ...）。</summary>
    Origin = 17,

    /// <summary>备注 / 特殊要求。</summary>
    Remarks = 18,

    /// <summary>客户 Logo（图片类字段，M1 可留空）。</summary>
    Logo = 19,
}

/// <summary>字段值的数据种类，决定格式校验与对齐默认值。</summary>
public enum MarkValueKind
{
    Text = 0,
    Integer = 1,
    Decimal = 2,
    /// <summary>重量（需带单位，量纲校验）。</summary>
    Weight = 3,
    /// <summary>体积 / 尺寸。</summary>
    Volume = 4,
    Date = 5,
    Image = 6,
}

/// <summary>数据来源。M6 的 AI 双通道交叉校验依赖它区分可信度来源。</summary>
public enum ValueOrigin
{
    /// <summary>用户在界面手工填写。</summary>
    Manual = 0,

    /// <summary>Excel/CSV 导入（M1 主路径，视为可信）。</summary>
    ExcelImport = 1,

    /// <summary>由编号规则引擎推导（如件号递增）。</summary>
    Rule = 2,

    /// <summary>本地 OCR 通道识别（M6）。</summary>
    AiOcr = 3,

    /// <summary>多模态大模型抽取（M6）——存在幻觉风险，须与 OCR 通道交叉校验。</summary>
    AiLlm = 4,
}

/// <summary>
/// 字段的静态定义（元数据）。
/// </summary>
/// <param name="Key">字段键。</param>
/// <param name="ChineseName">中文名，界面显示用。</param>
/// <param name="EnglishLabel">唛头上常见英文标记，模板默认文本用。</param>
/// <param name="Kind">数据种类。</param>
/// <param name="Aliases">表头自动匹配用的别名（不分大小写，含中英文与常见缩写）。</param>
/// <param name="Numeric">是否做数字/量纲白名单校验（防 AI 或脏数据写出负重量、零体积）。</param>
public sealed record FieldDefinition(
    MarkFieldKey Key,
    string ChineseName,
    string EnglishLabel,
    MarkValueKind Kind,
    IReadOnlyList<string> Aliases,
    bool Numeric = false);

/// <summary>
/// 唛头字段清单（唯一的字段真源）。
/// </summary>
public static class MarkFieldCatalog
{
    private static readonly List<FieldDefinition> Definitions = new()
    {
        new(MarkFieldKey.Consignee, "收货人/客户简称", "To", MarkValueKind.Text,
            new[] { "收货人", "客户", "客户简称", "致", "to", "consignee", "client", "buyer", "客户名称" }),

        new(MarkFieldKey.ClientCode, "客户代码", "C/S", MarkValueKind.Text,
            new[] { "客户代码", "客户编码", "company code", "client code", "cs" }),

        new(MarkFieldKey.ContractNo, "合同号/订单号", "Contract No.", MarkValueKind.Text,
            new[] { "合同号", "订单号", "合同编号", "单号", "contract", "contract no", "c/n", "cn", "order no", "po", "销售订单" }),

        new(MarkFieldKey.PoNumber, "PO 号", "PO No.", MarkValueKind.Text,
            new[] { "po号", "po no", "po number", "采购单号" }),

        new(MarkFieldKey.ItemNo, "货号/款号", "Item No.", MarkValueKind.Text,
            new[] { "货号", "款号", "品名", "商品编号", "item", "item no", "sku", "style", "style no", "art no" }),

        new(MarkFieldKey.DestinationPort, "目的港", "POD", MarkValueKind.Text,
            new[] { "目的港", "港口", "destination", "pod", "port of discharge", "dest port" }),

        new(MarkFieldKey.DestinationCountry, "目的国", "Destination", MarkValueKind.Text,
            new[] { "目的国", "国家", "destination country", "country" }),

        new(MarkFieldKey.CartonNo, "件号(本箱)", "No.", MarkValueKind.Integer,
            new[] { "件号", "箱号", "carton", "carton no", "no", "ctn", "ctn no", "box no", "本箱号", "序号" }, Numeric: true),

        new(MarkFieldKey.CartonTotal, "总件数", "of", MarkValueKind.Integer,
            new[] { "总件数", "总箱数", "件数合计", "total", "of", "total ctn", "cartons", "总数量" }, Numeric: true),

        new(MarkFieldKey.Quantity, "每箱数量", "PCS", MarkValueKind.Integer,
            new[] { "每箱数量", "数量", "pcs", "qty", "quantity", "每箱", "内装数量" }, Numeric: true),

        new(MarkFieldKey.GrossWeight, "毛重", "G.W.", MarkValueKind.Weight,
            new[] { "毛重", "g.w.", "gw", "gross weight", "毛重量" }, Numeric: true),

        new(MarkFieldKey.NetWeight, "净重", "N.W.", MarkValueKind.Weight,
            new[] { "净重", "n.w.", "nw", "net weight" }, Numeric: true),

        new(MarkFieldKey.Measurement, "体积", "Meas.", MarkValueKind.Volume,
            new[] { "体积", "cbm", "meas", "measurement", "volumn", "volume", "方数" }, Numeric: true),

        new(MarkFieldKey.BoxSize, "外箱尺寸", "L×W×H", MarkValueKind.Text,
            new[] { "尺寸", "外箱尺寸", "箱规", "规格", "size", "l×w×h", "l x w x h", "dimension" }),

        new(MarkFieldKey.BatchNo, "批次号", "Batch", MarkValueKind.Text,
            new[] { "批次", "批次号", "批号", "batch", "lot", "lot no" }),

        new(MarkFieldKey.ShipDate, "出货日期", "Date", MarkValueKind.Date,
            new[] { "出货日期", "日期", "交期", "date", "ship date", "shipping date" }),

        new(MarkFieldKey.Origin, "原产地", "Made in China", MarkValueKind.Text,
            new[] { "原产地", "产地", "origin", "made in", "made in china" }),

        new(MarkFieldKey.Remarks, "备注", "Remarks", MarkValueKind.Text,
            new[] { "备注", "说明", "特殊要求", "remark", "remarks", "note", "notes" }),

        new(MarkFieldKey.Logo, "客户Logo", "", MarkValueKind.Image,
            new[] { "logo", "标志", "图标", "image", "图片" }),
    };

    /// <summary>全部字段定义。</summary>
    public static IReadOnlyList<FieldDefinition> All => Definitions;

    /// <summary>可映射（非图片）的文本类字段，供映射界面与 AI 抽取使用。</summary>
    public static IReadOnlyList<FieldDefinition> Mappable { get; } =
        Definitions.Where(d => d.Kind != MarkValueKind.Image).ToList();

    /// <summary>字段总数，供 AI 生成模板时做枚举白名单规模校验。</summary>
    public static int Count => Definitions.Count;

    public static FieldDefinition Get(MarkFieldKey key)
        => Definitions.First(d => d.Key == key);

    public static bool TryGet(MarkFieldKey key, out FieldDefinition definition)
    {
        var found = Definitions.FirstOrDefault(d => d.Key == key);
        definition = found!;
        return found is not null;
    }

    /// <summary>按名字解析字段键（大小写不敏感），供 JSON 与模板表达式使用。</summary>
    public static bool TryParseKey(string? name, out MarkFieldKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        foreach (var d in Definitions)
        {
            if (string.Equals(d.Key.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                key = d.Key;
                return true;
            }
        }
        if (Enum.TryParse(trimmed, ignoreCase: true, out MarkFieldKey parsed)
            && Definitions.Any(d => d.Key == parsed))
        {
            key = parsed;
            return true;
        }
        return false;
    }

    /// <summary>该键是否为合法的唛头字段（AI 生成的模板 JSON 校验用）。</summary>
    public static bool IsKnownKey(string? name) => TryParseKey(name, out _);
}

/// <summary>
/// 单个字段值 + 它的<strong>来源与置信度元信息</strong>。
/// <para>
/// M1 只用到 <see cref="Origin"/>=<see cref="ValueOrigin.ExcelImport"/> 且 <see cref="NeedsReview"/>=false；
/// 结构现在就定下来，是为了 M6 的 AI 双通道交叉校验能直接落进同一模型：
/// 低置信度或双通道不一致 → <see cref="NeedsReview"/>=true → 界面标红、禁止未核对就落库/打印。
/// </para>
/// </summary>
public sealed class MarkValue
{
    public MarkValue(string? text, ValueOrigin origin = ValueOrigin.Manual)
    {
        Text = text?.Trim() ?? string.Empty;
        Origin = origin;
    }

    /// <summary>规范化后的文本值（永不为 null）。</summary>
    public string Text { get; }

    public ValueOrigin Origin { get; }

    /// <summary>0~1 的置信度；非 AI 来源可为 null 表示"不适用"。</summary>
    public double? Confidence { get; init; }

    /// <summary>是否需要人工核对（AI 来源默认 true，直到用户显式确认）。</summary>
    public bool NeedsReview { get; init; }

    /// <summary>人话化的告警原因，界面上作为红色提示文本。</summary>
    public string? Warning { get; init; }

    /// <summary>来源追溯，如 "工作表 Sheet1 第 3 列"。印刷数据必须可回溯到原始单元格。</summary>
    public string? SourceRef { get; init; }

    public bool IsEmpty => Text.Length == 0;

    /// <summary>来自 AI 通道（OCR 或多模态大模型）。</summary>
    public bool IsAiSourced => Origin is ValueOrigin.AiOcr or ValueOrigin.AiLlm;

    public MarkValue WithWarning(string warning) => new(Text, Origin)
    {
        Confidence = Confidence,
        NeedsReview = true,
        Warning = warning,
        SourceRef = SourceRef,
    };

    public override string ToString() => Text;
}

/// <summary>
/// 一条唛头记录（通常对应数据源的一行 = 一箱/一种箱唛）。
/// 不可变语义：修改通过 <see cref="MarkRecordBuilder"/> 产出新记录，避免界面与渲染层共享可变状态。
/// </summary>
public sealed class MarkRecord
{
    private readonly Dictionary<MarkFieldKey, MarkValue> _values;
    private readonly Dictionary<string, MarkValue> _custom;

    internal MarkRecord(
        Dictionary<MarkFieldKey, MarkValue> values,
        Dictionary<string, MarkValue> custom,
        int sourceRowIndex,
        string? sourceRef)
    {
        _values = values;
        _custom = custom;
        SourceRowIndex = sourceRowIndex;
        SourceRef = sourceRef;
    }

    /// <summary>数据源中的行号（1 起，跳过表头后的实际行序），用于回溯核对。</summary>
    public int SourceRowIndex { get; }

    /// <summary>来源文件/工作表的简述。</summary>
    public string? SourceRef { get; }

    public IReadOnlyDictionary<MarkFieldKey, MarkValue> Values => _values;

    /// <summary>未映射进标准字段的额外列，键形如 <c>col:列标题</c>。模板可用 {{{{col:xxx}}}} 引用。</summary>
    public IReadOnlyDictionary<string, MarkValue> CustomFields => _custom;

    public bool Has(MarkFieldKey key) => _values.TryGetValue(key, out var v) && !v.IsEmpty;

    public MarkValue? Get(MarkFieldKey key) => _values.TryGetValue(key, out var v) ? v : null;

    /// <summary>取文本值；无值返回空串。</summary>
    public string GetText(MarkFieldKey key) => _values.TryGetValue(key, out var v) ? v.Text : string.Empty;

    public MarkValue? GetCustom(string key) => _custom.TryGetValue(key, out var v) ? v : null;

    /// <summary>本条记录里所有需要人工核对的字段（M6 用；M1 恒为空）。</summary>
    public IEnumerable<KeyValuePair<MarkFieldKey, MarkValue>> PendingReview()
    {
        foreach (var kv in _values)
        {
            if (kv.Value.NeedsReview) yield return kv;
        }
    }

    public static MarkRecordBuilder Builder() => new();
}

/// <summary>构造 <see cref="MarkRecord"/> 的可变构建器。</summary>
public sealed class MarkRecordBuilder
{
    private readonly Dictionary<MarkFieldKey, MarkValue> _values = new();
    private readonly Dictionary<string, MarkValue> _custom = new(StringComparer.OrdinalIgnoreCase);
    private int _rowIndex;
    private string? _sourceRef;

    public MarkRecordBuilder SetRow(int rowIndex, string? sourceRef = null)
    {
        _rowIndex = rowIndex;
        _sourceRef = sourceRef;
        return this;
    }

    public MarkRecordBuilder Set(MarkFieldKey key, string? text, ValueOrigin origin = ValueOrigin.Manual)
    {
        if (string.IsNullOrWhiteSpace(text)) _values.Remove(key);
        else _values[key] = new MarkValue(text, origin);
        return this;
    }

    public MarkRecordBuilder Set(MarkFieldKey key, MarkValue value)
    {
        if (value.IsEmpty && !value.IsAiSourced) _values.Remove(key);
        else _values[key] = value;
        return this;
    }

    public MarkRecordBuilder SetCustom(string key, string? text, ValueOrigin origin = ValueOrigin.ExcelImport)
    {
        if (string.IsNullOrWhiteSpace(text)) _custom.Remove(key);
        else _custom[key] = new MarkValue(text, origin);
        return this;
    }

    public MarkRecord Build() => new(
        new Dictionary<MarkFieldKey, MarkValue>(_values),
        new Dictionary<string, MarkValue>(_custom, StringComparer.OrdinalIgnoreCase),
        _rowIndex,
        _sourceRef);
}
