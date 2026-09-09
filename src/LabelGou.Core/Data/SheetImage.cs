namespace LabelGou.Core.Data;

/// <summary>
/// 工作表里贴着的一张图（模板截图 / 效果照片）以及它锚在网格的哪个位置。
/// <para><strong>为什么要它</strong>（2026-09-09 用户定：「AI 查看表格——一般表格右侧标注了模板或者效果照片」）：
/// 工厂发来的唛头表，右侧那张贴图往往就是「这枚标签该长什么样」的唯一说明，
/// 而它不是任何一个单元格的值。以前读取器只解 <c>worksheets/sheetN.xml</c>，
/// 图对软件压根不存在，AI 于是只能凭列名猜版式（猜错就印错货）。</para>
/// <para>锚点必须一起给：模型需要知道「第 12 列旁边那张图，是说这一带这几行的版式」，
/// 否则一堆图就是一堆无上下文的像素。</para>
/// </summary>
/// <param name="PartPath">zip 内的实际条目路径（如 <c>xl/media/image3.png</c>），用于日志与去重。</param>
/// <param name="FileName">媒体文件名（原样，不含目录）。</param>
/// <param name="MimeType">按真实扩展名给的 MIME（发模型时用，不能一律写 png）。</param>
/// <param name="Bytes">图片原始字节（未降采样，交给发送前那道 <c>ImageForModel</c> 处理）。</param>
/// <param name="AnchorRow">锚点起始行（0 起）；绝对锚点没有 <c>from</c> 时是 -1。</param>
/// <param name="AnchorColumn">锚点起始列（0 起）；同上 -1。</param>
/// <param name="AltName">作者给图起的名字（<c>cNvPr@name</c>，中文表里常写「图片 12」或干脆是文件主题）。</param>
public sealed record SheetImage(
    string PartPath,
    string FileName,
    string MimeType,
    byte[] Bytes,
    int AnchorRow,
    int AnchorColumn,
    string? AltName)
{
    /// <summary>字节数（日志/上限判断用，不让一张原图把提示词撑爆）。</summary>
    public int ByteLength => Bytes.Length;

    /// <summary>摊给模型和界面看的一行：图在哪、多大、叫什么。</summary>
    public string Describe()
    {
        var where = AnchorRow < 0 || AnchorColumn < 0
            ? "位置未知（浮动图）"
            : $"贴在原表第 {AnchorRow + 1} 行、第 {AnchorColumn + 1} 列那一格起";
        var name = string.IsNullOrWhiteSpace(AltName) ? FileName : $"{FileName}「{AltName}」";
        return $"{name}：{where}，{Bytes.Length / 1024} KB";
    }
}
