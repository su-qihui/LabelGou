using LabelGou.Core.Data;
using LabelGou.Core.Marks;
using LabelGou.Core.Numbering;

namespace LabelGou.Core.Mapping;

/// <summary>一条"标准字段 ← 数据列"的映射。</summary>
public sealed class FieldMapping
{
    /// <summary>目标唛头字段。</summary>
    public MarkFieldKey Field { get; set; }

    /// <summary>数据列下标；-1 表示该字段暂不映射。</summary>
    public int ColumnIndex { get; set; } = -1;

    /// <summary>
    /// 列标题快照。列顺序变了但仍同名时，可据此自动重连（比纯下标稳）。
    /// </summary>
    public string? ColumnHeader { get; set; }

    public bool IsBound => ColumnIndex >= 0;
}

/// <summary>
/// 一份可保存/复用的映射方案：把某类表格的列绑定到唛头标准字段。
/// <para>
/// <see cref="HeaderSignature"/> 记住这套方案适配的表头特征，下次遇到同格式自动推荐，
/// 让打印店"同一客户反复来单"这种高频场景一次配置、长期复用。
/// </para>
/// </summary>
public sealed class MappingProfile
{
    public string Name { get; set; } = "未命名方案";

    /// <summary>备注，如"XX 厂装箱单格式"。</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>表头特征签名（归一化表头拼接后取哈希），用于自动匹配方案。</summary>
    public string HeaderSignature { get; set; } = string.Empty;

    /// <summary>创建时记录的原始表头，便于人读与重连。</summary>
    public List<string> HeaderHints { get; set; } = new();

    public List<FieldMapping> Mappings { get; set; } = new();

    /// <summary>
    /// <strong>整批固定值</strong>：表里根本没有这一列、但整批标签都要印同一个值。
    /// <para>
    /// 厂商表里的客户名（如 BOLAROM）就是典型：它是“这一批货”的属性，不是“这一行”的属性，
    /// 所以表里没有对应的列，行式模板的第一行因此始终印不出来（旧行为：空行被丢掉）。
    /// 键 = <see cref="MarkFieldKey"/> 的名字（JSON 存字符串，枚举改顺序不会错位），值 = 要印的文本。
    /// </para>
    /// <para>只在字段<strong>没有</strong>表格值时兜底：固定值是补空，不是覆盖真数据。</para>
    /// </summary>
    public Dictionary<string, string> FixedValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取某字段的整批固定值；没填返回 null。</summary>
    public string? FixedValueFor(MarkFieldKey field)
        => FixedValues.TryGetValue(field.ToString(), out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim() : null;

    /// <summary>设/改整批固定值；传空白即移除。</summary>
    public void SetFixedValue(MarkFieldKey field, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) FixedValues.Remove(field.ToString());
        else FixedValues[field.ToString()] = text.Trim();
        UpdatedAt = DateTime.Now;
    }

    /// <summary>是否对没有件号数据的行自动按序号补齐（来源标记为 <see cref="ValueOrigin.Rule"/>）。</summary>
    public bool AutoNumberCartons { get; set; } = true;

    /// <summary>
    /// 该客户惯用的<strong>件号编号规则</strong>（M2）。
    /// <para>
    /// 存在方案里而不是单独建库：打印店的复用单位是“同一客户的同一张表”，
    /// 件号习惯（起始号、补零、按合同号分组）本来就是跟客户走的。旧 JSON 没这个字段时按默认规则跑。
    /// </para>
    /// </summary>
    public NumberingRule? Numbering { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>取某字段绑定的列下标；未绑定返回 -1。</summary>
    public int ColumnIndexOf(MarkFieldKey field)
        => Mappings.FirstOrDefault(m => m.Field == field && m.IsBound)?.ColumnIndex ?? -1;

    public FieldMapping MappingFor(MarkFieldKey field)
    {
        var existing = Mappings.FirstOrDefault(m => m.Field == field);
        if (existing is not null) return existing;
        var created = new FieldMapping { Field = field, ColumnIndex = -1 };
        Mappings.Add(created);
        return created;
    }

    /// <summary>绑定/改绑；<paramref name="columnIndex"/> 传 -1 即解绑。</summary>
    public void Bind(MarkFieldKey field, int columnIndex, IReadOnlyList<string>? headers = null)
    {
        var mapping = MappingFor(field);
        mapping.ColumnIndex = columnIndex;
        mapping.ColumnHeader = columnIndex >= 0 && headers is not null && columnIndex < headers.Count
            ? headers[columnIndex]
            : null;
        UpdatedAt = DateTime.Now;
    }

    /// <summary>已被任何字段占用的列下标集合。</summary>
    public HashSet<int> BoundColumns()
        => Mappings.Where(m => m.IsBound).Select(m => m.ColumnIndex).ToHashSet();

    public int BoundCount => Mappings.Count(m => m.IsBound);

    /// <summary>由表头计算签名。</summary>
    public static string ComputeSignature(IReadOnlyList<string> headers)
    {
        var joined = string.Join("|", headers.Select(HeaderRowDetector.Normalize));
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>按表头新建一份空方案（含全部字段的占位映射，界面直接可绑）。</summary>
    public static MappingProfile CreateFor(IReadOnlyList<string> headers, string name = "新映射方案")
    {
        var profile = new MappingProfile
        {
            Name = name,
            HeaderSignature = ComputeSignature(headers),
            HeaderHints = headers.ToList(),
        };
        foreach (var def in MarkFieldCatalog.All)
        {
            profile.Mappings.Add(new FieldMapping { Field = def.Key, ColumnIndex = -1 });
        }
        return profile;
    }
}

/// <summary>一次映射的产出与告警汇总。</summary>
public sealed class MappingResult
{
    public MappingResult(IReadOnlyList<MarkRecord> records, IReadOnlyList<MappingIssue> issues)
    {
        Records = records;
        Issues = issues;
    }

    public IReadOnlyList<MarkRecord> Records { get; }

    /// <summary>数据质量告警（脏数据、量纲异常等），界面列出但不阻断。</summary>
    public IReadOnlyList<MappingIssue> Issues { get; }

    public int Count => Records.Count;
}

/// <summary>
/// 一条数据质量告警。
/// </summary>
/// <param name="RowNumber">数据行号（1 起，界面上"第 N 条"）。</param>
/// <param name="Field">涉及字段，null 表示整行问题。</param>
/// <param name="Message">人话描述。</param>
/// <param name="Severity">级别。</param>
public sealed record MappingIssue(
    int RowNumber,
    MarkFieldKey? Field,
    string Message,
    IssueSeverity Severity);

public enum IssueSeverity
{
    /// <summary>提示：可忽略。</summary>
    Info = 0,

    /// <summary>警告：疑似脏数据，建议核对。</summary>
    Warning = 1,

    /// <summary>错误：明显不可能（负重量等），必须核对才能印。</summary>
    Error = 2,
}
