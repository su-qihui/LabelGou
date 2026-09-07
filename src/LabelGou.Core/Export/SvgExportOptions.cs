namespace LabelGou.Core.Export;

/// <summary>SVG 出口的成件方式。</summary>
public enum SvgExportMode
{
    /// <summary>一页一个文件（含纸规与角线），拿回去就能出片。</summary>
    PerSheet = 0,

    /// <summary>一枚标签一个文件（画布就是标签尺寸），供对方在 CorelDRAW 里自己拼大版。</summary>
    PerLabel = 1,
}

/// <summary>
/// SVG 导出的口径（纯数据，App 层的写出器与界面复选框都消费它）。
/// <para>
/// 图层开关按<strong>出片方实际会动的东西</strong>分：裁切角线、套准十字、刀框、注记各成一层，
/// 在 CorelDRAW 里要哪种就整层删掉，不用一个元素一个元素点。
/// </para>
/// </summary>
public sealed class SvgExportOptions
{
    public SvgExportMode Mode { get; init; } = SvgExportMode.PerSheet;

    /// <summary>是否画纸张外框（<c>sheet</c> 层）。拼版校位时开，交付时通常关。</summary>
    public bool IncludePaperFrame { get; init; }

    public bool IncludeCropMarks { get; init; } = true;

    public bool IncludeRegistrationMarks { get; init; } = true;

    /// <summary>刀模/标签轮廓示意线（默认不带：那是给人看的，不是要刻的）。</summary>
    public bool IncludeLabelOutlines { get; init; }

    /// <summary>注记层：元素参考框 + 字段名，默认关，可整层删。</summary>
    public bool IncludeNotes { get; init; }

    /// <summary>
    /// 文字是否转曲（定案「导出默认转曲」）。
    /// <para>true：字形变成 <c>&lt;path&gt;</c>，所见即所得且不怕对方机器缺中文字体，代价是不能在 CDR 里改字。
    /// false：写 <c>&lt;text&gt;</c>，可改字，但预览端做过的缩字号与省略号截断在 CDR 里会还原不上。</para>
    /// </summary>
    public bool TextAsOutlines { get; init; } = true;

    /// <summary>位图是否以 base64 内嵌（false 则写成 <c>file:///</c> 绝对路径引用，图必须跟着走）。</summary>
    public bool EmbedRasterImages { get; init; } = true;

    /// <summary>单张内嵌位图的体积上限，超过就改用文件引用并在摘要里说明（否则一个 Logo 能把 SVG 撑到几十 MB）。</summary>
    public long MaxEmbeddedImageBytes { get; init; } = 24L * 1024 * 1024;

    /// <summary>写进 <c>&lt;metadata&gt;</c> 的生成者标识。</summary>
    public string? Producer { get; init; }

    /// <summary>写进 <c>&lt;metadata&gt;</c> 的任务名。</summary>
    public string? Title { get; init; }

    public static SvgExportOptions Default { get; } = new();

    /// <summary>给用户看得懂的口径说明（导出摘要与 ⑥ 面板都用它，避免界面与实现各写一套）。</summary>
    public string Describe()
    {
        // 一枚一图时纸层面的东西根本不会被写进去（画布就是一枚标签），
        // 所以这里也不能报“有角线”——口径说明必须和件子里真有的东西一致。
        var onSheet = Mode == SvgExportMode.PerSheet;
        var layers = new List<string>();
        if (IncludePaperFrame && onSheet) layers.Add("纸框");
        if (IncludeCropMarks && onSheet) layers.Add("角线");
        if (IncludeRegistrationMarks && onSheet) layers.Add("套准");
        if (IncludeLabelOutlines && onSheet) layers.Add("刀框");
        if (IncludeNotes) layers.Add("注记");
        return string.Join(" · ",
            onSheet ? "整页一图" : "一枚一图",
            TextAsOutlines ? "文字转曲" : "文字保留可编辑",
            EmbedRasterImages ? "图片内嵌" : "图片引用",
            layers.Count == 0 ? "仅内容" : "图层：" + string.Join("/", layers));
    }

    /// <summary>开口的合法性检查（与 <c>SheetExportRequest.CollectIssues</c> 同一口径：只报必须报的）。</summary>
    public void CollectIssues(IList<string> issues)
    {
        if (!TextAsOutlines)
        {
            issues.Add("已关闭文字转曲：CorelDRAW 端若缺中文字体会掉字，且预览里的缩字号/省略号在对方那台机器上会还原成原始字号。");
        }
        if (!EmbedRasterImages)
        {
            issues.Add("图片改为文件引用：SVG 单独拷走会丢图，请与图片放同一台机器上打开。");
        }
        if (Mode == SvgExportMode.PerLabel
            && (IncludePaperFrame || IncludeCropMarks || IncludeRegistrationMarks || IncludeLabelOutlines))
        {
            issues.Add("一枚一图模式下没有“纸”可放，纸框/角线/套准/刀框这几层不会写出（拼版时由对方自己加）。");
        }
    }
}
