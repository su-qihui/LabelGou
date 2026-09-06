using System.Text.Json.Serialization;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Templates;

public enum ElementKind
{
    /// <summary>文本（可含变量占位符）。</summary>
    Text = 0,
    /// <summary>直线（分隔线/边框段）。</summary>
    Line = 1,
    /// <summary>矩形边框。</summary>
    Rect = 2,
    /// <summary>图片（如客户 Logo、条码图）。</summary>
    Image = 3,
}

public enum HorizontalAlign
{
    Left = 0,
    Center = 1,
    Right = 2,
}

/// <summary>
/// 模板元素。<strong>坐标一律毫米、字号一律磅</strong>，绝不用像素——
/// 这样同一份模板在预览、打印、PDF、图片导出里尺寸一致。
/// </summary>
public sealed class TemplateElement
{
    public ElementKind Kind { get; set; } = ElementKind.Text;

    /// <summary>左上角 X（毫米，相对标签左上角）。</summary>
    public double X { get; set; }

    /// <summary>左上角 Y（毫米）。</summary>
    public double Y { get; set; }

    /// <summary>宽（毫米）。对 Line 表示起点到终点的水平跨度由 X2/Y2 决定，此字段忽略。</summary>
    public double Width { get; set; } = 20;

    /// <summary>高（毫米）。</summary>
    public double Height { get; set; } = 6;

    /// <summary>Line 的终点 X（毫米）。</summary>
    public double X2 { get; set; }

    /// <summary>Line 的终点 Y（毫米）。</summary>
    public double Y2 { get; set; }

    /// <summary>
    /// 文本内容。支持变量占位符 <c>{{字段键}}</c>、<c>{{col:列标题}}</c>
    /// 与内置计算量 <c>{{NoXofY}}</c>、<c>{{CartonNo}}</c>、<c>{{RowIndex}}</c> 等。
    /// </summary>
    public string? Text { get; set; }

    /// <summary>图片相对路径（相对模板文件所在目录），Kind=Image 时使用。</summary>
    public string? ImagePath { get; set; }

    public string FontFamily { get; set; } = DefaultFont;

    /// <summary>字号（磅）。</summary>
    public double FontSizePt { get; set; } = 8;

    public bool Bold { get; set; }

    public HorizontalAlign Align { get; set; } = HorizontalAlign.Left;

    /// <summary>线宽/边框粗细（毫米）。印刷常用 0.25~0.5。</summary>
    public double ThicknessMm { get; set; } = 0.35;

    /// <summary>文本框内容放不下时是否允许自动缩字号（渲染端执行，引擎只带标志）。</summary>
    public bool ShrinkToFit { get; set; } = true;

    /// <summary>最多几行，超出后截断加省略号。0 表示不限。</summary>
    public int MaxLines { get; set; } = 3;

    public bool Visible { get; set; } = true;

    /// <summary>默认字体：微软雅黑，Win10/11 自带，中英混排都不会掉字。</summary>
    public const string DefaultFont = "Microsoft YaHei";

    [JsonIgnore]
    public double AreaMm2 => Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// 一份唛头标签模板（A 类内置 / B 类用户拖拽 / C 类 CDR 底稿，共用同一结构）。
/// <para>
/// 这个类同时是 <strong>M7「AI 生成模板」的输出契约</strong>：大模型只允许产出符合本结构
/// 且能通过 <see cref="TemplateValidator.Validate"/> 的 JSON，任何越界、未知字段、超大字号
/// 都在校验阶段拒绝，AI 不允许直接把毫米坐标写进印面。
/// </para>
/// </summary>
public sealed class LabelTemplate
{
    /// <summary>当前 schema 版本。改结构必须递增，并让 <see cref="TemplateValidator"/> 兼容旧版。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>稳定标识，如 <c>builtin.standard-100x80</c>。用户模板用 <c>user.xxx</c>。</summary>
    public string Id { get; set; } = "user." + Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "未命名模板";

    public string Note { get; set; } = string.Empty;

    /// <summary>标签宽（毫米）。</summary>
    public double WidthMm { get; set; } = 100;

    /// <summary>标签高（毫米）。</summary>
    public double HeightMm { get; set; } = 80;

    /// <summary>内边距（毫米）。B 类编辑器拖拽时的对齐基准。</summary>
    public double PaddingMm { get; set; } = 4;

    /// <summary>0 表示不画外框；否则为外框线宽（毫米）。</summary>
    public double BorderMm { get; set; } = 0.5;

    /// <summary>
    /// 保留字段（旧 schema 兼容用）：<strong>整版裁切线由纸张/刀模决定，不由内容模板决定</strong>，
    /// 拼版时实际生效的是 <c>SheetSpec.CropMarks</c>，本字段不参与落位。
    /// </summary>
    public double CropMarkMm { get; set; }

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>true 表示内置模板，不允许被保存覆盖。</summary>
    public bool BuiltIn { get; set; }

    public List<TemplateElement> Elements { get; set; } = new();

    /// <summary>版面方向（仅用于界面提示与实际打印时的纸张方向，M2/M3 消费）。</summary>
    public string Description => $"{Name}（{WidthMm:0.#} × {HeightMm:0.#} mm）";

    public LabelTemplate CloneAsUserCopy(string newName)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, Mapping.ProfileStore.JsonOptions);
        var copy = System.Text.Json.JsonSerializer.Deserialize<LabelTemplate>(json, Mapping.ProfileStore.JsonOptions)!;
        copy.Id = "user." + Guid.NewGuid().ToString("N")[..8];
        copy.Name = newName;
        copy.BuiltIn = false;
        return copy;
    }
}

/// <summary>模板校验问题。</summary>
/// <param name="Severity">级别。</param>
/// <param name="Message">人话描述。</param>
/// <param name="ElementIndex">涉及元素下标，-1 表示模板级问题。</param>
public sealed record TemplateIssue(IssueLevel Severity, string Message, int ElementIndex = -1);

public enum IssueLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// 模板校验器：AI/M7 与人工编辑（B 类）的<strong>共同闸门</strong>。
/// <para>
/// 规则设计成"能印就印得对"的下限：元素不越界、字号不夸张、变量名合法、结构不超载。
/// 大模型生成的模板 JSON 只要这里报 Error，一律拒绝入模板库。
/// </para>
/// </summary>
public static class TemplateValidator
{
    /// <summary>单张标签允许的最大边长（毫米）。超过基本是误填。</summary>
    public const double MaxLabelSideMm = 600;

    /// <summary>允许的最小边长。</summary>
    public const double MinLabelSideMm = 8;

    /// <summary>字号上下限（磅）。</summary>
    public const double MinFontPt = 3;

    public const double MaxFontPt = 60;

    /// <summary>元素数量上限，防呆也防 AI 无限堆。</summary>
    public const int MaxElements = 80;

    /// <summary>坐标比对容差（毫米），0.05mm 以内不算越界。</summary>
    public const double ToleranceMm = 0.05;

    public static IReadOnlyList<TemplateIssue> Validate(LabelTemplate template)
    {
        var issues = new List<TemplateIssue>();
        if (template is null)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error, "模板为空。"));
            return issues;
        }

        if (template.SchemaVersion > LabelTemplate.CurrentSchemaVersion)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error,
                $"模板版本 v{template.SchemaVersion} 高于当前程序支持的 v{LabelTemplate.CurrentSchemaVersion}，请升级 LabelGou。"));
        }

        if (template.WidthMm is < MinLabelSideMm or > MaxLabelSideMm)
            issues.Add(new TemplateIssue(IssueLevel.Error, $"标签宽度 {template.WidthMm:0.#} mm 不在 {MinLabelSideMm}~{MaxLabelSideMm} mm 之间。"));
        if (template.HeightMm is < MinLabelSideMm or > MaxLabelSideMm)
            issues.Add(new TemplateIssue(IssueLevel.Error, $"标签高度 {template.HeightMm:0.#} mm 不在 {MinLabelSideMm}~{MaxLabelSideMm} mm 之间。"));
        if (template.PaddingMm < 0)
            issues.Add(new TemplateIssue(IssueLevel.Error, "内边距不能为负。"));

        if (template.Elements.Count == 0)
            issues.Add(new TemplateIssue(IssueLevel.Warning, "模板里还没有任何元素，预览会是空白。"));
        if (template.Elements.Count > MaxElements)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error,
                $"元素数量 {template.Elements.Count} 超过上限 {MaxElements}。"));
        }

        for (var i = 0; i < template.Elements.Count; i++)
        {
            var e = template.Elements[i];
            var tag = $"第 {i + 1} 个元素";

            if (e.X < -ToleranceMm || e.Y < -ToleranceMm)
                issues.Add(new TemplateIssue(IssueLevel.Error, $"{tag} 起点超出标签左上角（X={e.X:0.#}, Y={e.Y:0.#} mm）。", i));

            if (e.Kind != ElementKind.Line)
            {
                if (e.Width <= 0 || e.Height <= 0)
                    issues.Add(new TemplateIssue(IssueLevel.Error, $"{tag} 宽高必须大于 0。", i));
                if (e.X + e.Width > template.WidthMm + ToleranceMm)
                    issues.Add(new TemplateIssue(IssueLevel.Error,
                        $"{tag} 右侧越界 {e.X + e.Width - template.WidthMm:0.##} mm（标签宽 {template.WidthMm:0.#} mm）。", i));
                if (e.Y + e.Height > template.HeightMm + ToleranceMm)
                    issues.Add(new TemplateIssue(IssueLevel.Error,
                        $"{tag} 下方越界 {e.Y + e.Height - template.HeightMm:0.##} mm（标签高 {template.HeightMm:0.#} mm）。", i));
            }
            else
            {
                if (Math.Abs(e.X - e.X2) < ToleranceMm && Math.Abs(e.Y - e.Y2) < ToleranceMm)
                    issues.Add(new TemplateIssue(IssueLevel.Warning, $"{tag} 是一条零长度线段，不会显示。", i));
                if (e.X2 > template.WidthMm + ToleranceMm || e.Y2 > template.HeightMm + ToleranceMm
                    || e.X2 < -ToleranceMm || e.Y2 < -ToleranceMm)
                    issues.Add(new TemplateIssue(IssueLevel.Error, $"{tag} 线段端点超出标签范围。", i));
            }

            if (e.Kind == ElementKind.Text)
            {
                if (e.FontSizePt is < MinFontPt or > MaxFontPt)
                    issues.Add(new TemplateIssue(IssueLevel.Error,
                        $"{tag} 字号 {e.FontSizePt:0.#}pt 不在 {MinFontPt}~{MaxFontPt}pt 之间。", i));

                foreach (var token in TemplateTokenizer.EnumerateTokens(e.Text))
                {
                    if (TemplateTokenizer.IsBuiltInToken(token)) continue;
                    if (token.StartsWith("col:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!MarkFieldCatalog.TryParseKey(token, out _))
                    {
                        issues.Add(new TemplateIssue(IssueLevel.Error,
                            $"{tag} 引用了未知字段「{token}」，可用字段见字段清单。", i));
                    }
                }
            }

            if (e.Kind == ElementKind.Image && string.IsNullOrWhiteSpace(e.ImagePath))
                issues.Add(new TemplateIssue(IssueLevel.Warning, $"{tag} 是图片元素但没有指定图片文件。", i));

            if (e.ThicknessMm <= 0)
                issues.Add(new TemplateIssue(IssueLevel.Warning, $"{tag} 线宽为 0，打印时不会显示。", i));
            else if (e.ThicknessMm > 3)
                issues.Add(new TemplateIssue(IssueLevel.Warning, $"{tag} 线宽 {e.ThicknessMm:0.##} mm 偏粗，不干胶上容易糊。", i));
        }

        return issues;
    }

    public static bool HasError(this IReadOnlyList<TemplateIssue> issues)
        => issues.Any(i => i.Severity == IssueLevel.Error);

    /// <summary>只取需要修掉的错误信息文本。</summary>
    public static IReadOnlyList<string> ErrorMessages(this IReadOnlyList<TemplateIssue> issues)
        => issues.Where(i => i.Severity == IssueLevel.Error).Select(i => i.Message).ToList();
}
