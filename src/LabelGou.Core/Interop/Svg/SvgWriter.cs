using System.Globalization;
using System.Text;
using LabelGou.Core.Units;

namespace LabelGou.Core.Interop.Svg;

/// <summary>
/// SVG 文本生成器（M5 的两个出口共用的底层笔）：
/// <strong>1 用户单位 = 1 毫米</strong>，<c>width/height</c> 直接写 <c>…mm</c>，
/// 这样 CorelDRAW/Illustrator 打开就是 1:1 实物尺寸，不需要任何缩放手续。
/// <para>
/// 图层用顶层 <c>&lt;g id="…"&gt;</c> 表达（CDR 导入 SVG 会把它们认成可整层选/删的组合），
/// 所以角线、套准十字、内容、注记各成一层，对方要哪种就删哪种。
/// </para>
/// </summary>
public sealed class SvgBuilder
{
    private readonly StringBuilder _body = new();
    private readonly Stack<string> _layers = new();
    private readonly List<string> _metadata = new();

    public SvgBuilder(double widthMm, double heightMm, string? generatorComment = null)
    {
        WidthMm = widthMm;
        HeightMm = heightMm;
        if (!string.IsNullOrWhiteSpace(generatorComment)) Comment(generatorComment);
    }

    public double WidthMm { get; }
    public double HeightMm { get; }

    /// <summary>已写入的元素数（导出摘要要说"这一页画了多少东西"）。</summary>
    public int ElementCount { get; private set; }

    public SvgBuilder Comment(string text)
    {
        _body.Append("<!-- ").Append(Escaped(text)).Append(" -->\n");
        return this;
    }

    /// <summary>往 <c>&lt;metadata&gt;</c> 里塞一行说明（导出时把字段清单放这儿，CDR 里看不见但不影响出片）。</summary>
    public SvgBuilder Metadata(string text)
    {
        _metadata.Add(text);
        return this;
    }

    public SvgBuilder StartLayer(string id, string? label = null, string? transform = null)
    {
        _layers.Push(id);
        _body.Append(Indent()).Append("<g id=\"").Append(Attr(id)).Append('"');
        if (!string.IsNullOrWhiteSpace(label)) _body.Append(" inkscape:label=\"").Append(Attr(label)).Append('"');
        if (!string.IsNullOrWhiteSpace(transform)) _body.Append(" transform=\"").Append(Attr(transform)).Append('"');
        _body.Append(">\n");
        return this;
    }

    /// <summary>
    /// 一个带 transform 的普通分组（第 43 棒：旋转/拉伸的文字与图片用它包住，几何变换只写这一层）。
    /// <para>与 <see cref="StartLayer"/> 的区别只是不占图层语义：CDR 打开时它是"对象分组"，
    /// 整组能一起选中，变换由组自己带着——与预览端 PushTransform 一一对应。</para>
    /// </summary>
    public SvgBuilder StartGroup(string transform)
    {
        _layers.Push("group");
        _body.Append(Indent()).Append("<g transform=\"").Append(Attr(transform)).Append("\">\n");
        return this;
    }

    /// <summary>转成 SVG 的 <c>transform</c> 串（毫米）。目标合成 = 先绕元素中心拉伸、再绕元素中心旋转，
    /// 与 <c>LabelRenderer.PushGeometry</c> 逐条同序同锚点。
    /// <para>SVG 的 <c>transform</c> 列表【最右先作用到点上】，所以"平移进中心"要写在最左、"平移回原点"写在最右：
    /// <c>translate(c) [rotate] [scale] translate(-c)</c>。只转不拉 / 只拉不转 / 都无 三种退化情形由空段自动省掉。</para>
    /// </summary>
    public static string GeometryTransform(double centerXmm, double centerYmm, double rotationDeg, double scaleX, double scaleY)
    {
        var stretch = Math.Abs(scaleX - 1) > 1e-6 || Math.Abs(scaleY - 1) > 1e-6;
        var rotate = Math.Abs(rotationDeg) > 1e-6;
        if (!stretch && !rotate) return string.Empty;

        var c = $"{N(centerXmm)},{N(centerYmm)}";
        var sb = new StringBuilder($"translate({c})");
        if (rotate) sb.Append($" rotate({N(rotationDeg)})");
        if (stretch) sb.Append($" scale({N(scaleX)},{N(scaleY)})");
        sb.Append($" translate(-{N(centerXmm)},-{N(centerYmm)})");
        return sb.ToString();
    }

    public SvgBuilder EndLayer()
    {
        if (_layers.Count == 0) throw new InvalidOperationException("没有可结束的图层。");
        _layers.Pop();
        _body.Append(Indent()).Append("</g>\n");
        return this;
    }

    /// <summary>一条路径（毫米坐标，命令已是绝对 M/L/C/Z）。</summary>
    public SvgBuilder Path(IReadOnlyList<SvgPathCommand> commands, SvgPaint? fill, SvgPaint? stroke, bool evenOdd = false)
        => Path(PaintAttributes(fill, stroke, evenOdd), DataOf(commands));

    public SvgBuilder Path(string paintAttributes, string data)
    {
        _body.Append(Indent()).Append("<path d=\"").Append(data).Append("\" ").Append(paintAttributes).Append("/>\n");
        ElementCount++;
        return this;
    }

    public SvgBuilder Line(double x1, double y1, double x2, double y2, SvgPaint stroke)
    {
        _body.Append(Indent()).Append("<line x1=\"").Append(N(x1)).Append("\" y1=\"").Append(N(y1))
            .Append("\" x2=\"").Append(N(x2)).Append("\" y2=\"").Append(N(y2)).Append("\" ")
            .Append(PaintAttributes(null, stroke, false)).Append("/>\n");
        ElementCount++;
        return this;
    }

    public SvgBuilder Rect(double x, double y, double width, double height, SvgPaint? fill, SvgPaint? stroke, double rxMm = 0)
    {
        _body.Append(Indent()).Append("<rect x=\"").Append(N(x)).Append("\" y=\"").Append(N(y))
            .Append("\" width=\"").Append(N(width)).Append("\" height=\"").Append(N(height));
        if (rxMm > 0) _body.Append("\" rx=\"").Append(N(rxMm));
        _body.Append("\" ").Append(PaintAttributes(fill, stroke, false)).Append("/>\n");
        ElementCount++;
        return this;
    }

    /// <summary>椭圆/圆（第 51 棒）：发原生 <c>&lt;ellipse&gt;</c>——CDR 认它，也比四段 kappa 贝塞尔少一层近似。</summary>
    public SvgBuilder Ellipse(double centerXmm, double centerYmm, double radiusXmm, double radiusYmm, SvgPaint? fill, SvgPaint? stroke)
    {
        _body.Append(Indent()).Append("<ellipse cx=\"").Append(N(centerXmm)).Append("\" cy=\"").Append(N(centerYmm))
            .Append("\" rx=\"").Append(N(radiusXmm)).Append("\" ry=\"").Append(N(radiusYmm))
            .Append("\" ").Append(PaintAttributes(fill, stroke, false)).Append("/>\n");
        ElementCount++;
        return this;
    }

    /// <summary>正多边形（第 51 棒）：发原生 <c>&lt;polygon&gt;</c>，点表由调用方给（Core 是唯一出处，这里只格式化，不再算一个点）。</summary>
    public SvgBuilder Polygon(IReadOnlyList<(double X, double Y)> points, SvgPaint? fill, SvgPaint? stroke)
    {
        var list = new System.Text.StringBuilder();
        foreach (var (px, py) in points)
        {
            if (list.Length > 0) list.Append(' ');
            list.Append(N(px)).Append(',').Append(N(py));
        }
        _body.Append(Indent()).Append("<polygon points=\"").Append(list).Append("\" ")
            .Append(PaintAttributes(fill, stroke, false)).Append("/>\n");
        ElementCount++;
        return this;
    }

    /// <summary>一段矢量轮廓文字（<paramref name="pathData"/> 由调用方用 <c>FormattedText.BuildGeometry()</c> 现算）。</summary>
    public SvgBuilder GlyphPath(string pathData, SvgPaint fill, double opacityOverride = 1)
    {
        var paint = fill with { Opacity = fill.Opacity * opacityOverride };
        _body.Append(Indent()).Append("<path d=\"").Append(pathData).Append("\" ").Append(PaintAttributes(paint, null, false)).Append("/>\n");
        ElementCount++;
        return this;
    }

    /// <summary>内嵌位图（base64 data URI，导出单文件自带图，不让对方去找图）。</summary>
    public SvgBuilder Image(byte[] bytes, string mimeType, double x, double y, double width, double height)
        => ImageInner(Convert.ToBase64String(bytes), true, mimeType, x, y, width, height);

    /// <summary>
    /// 引用外部位图（写 <c>file:///</c> 或相对路径，不内嵌）。
    /// <para>图太大时的退路：宁可写成引用并在摘要里说清“图要跟着走”，也不把 SVG 撑到几十 MB。</para>
    /// </summary>
    public SvgBuilder ImageRef(string href, double x, double y, double width, double height)
        => ImageInner(href, false, null, x, y, width, height);

    private SvgBuilder ImageInner(string hrefValue, bool base64DataUri, string? mimeType, double x, double y, double width, double height)
    {
        var target = base64DataUri
            ? "data:" + (mimeType ?? "image/png") + ";base64," + hrefValue
            : hrefValue;
        _body.Append(Indent()).Append("<image ")
            .Append(AttrPair("x", N(x))).Append(' ')
            .Append(AttrPair("y", N(y))).Append(' ')
            .Append(AttrPair("width", N(width))).Append(' ')
            .Append(AttrPair("height", N(height))).Append(' ')
            .Append(AttrPair("href", target)).Append(' ')
            .Append(AttrPair("preserveAspectRatio", "none"));
        _body.Append("/>\n");
        ElementCount++;
        return this;
    }

    /// <summary>一个 <c>名="值"</c> 属性对（值里的引号与 &amp; 已转义）。</summary>
    private static string AttrPair(string name, string value) => name + "=\"" + Attr(value) + "\"";

    /// <summary>
    /// 可编辑文字。<strong>M5 定案（乙）：默认不走这条</strong>，导出用 <see cref="GlyphPath"/> 转曲，
    /// 因为 <c>&lt;text&gt;</c> 反映不了预览端 <c>FormattedText</c> 内部做的缩字号与省略号截断。
    /// 留着是给"要在 CDR 里改字"的场景按需开启。
    /// </summary>
    public SvgBuilder Text(string content, double xMm, double baselineYmm, double sizePt, string fontFamily, bool bold, SvgTextAnchor anchor, SvgPaint? fill)
    {
        _body.Append(Indent()).Append("<text x=\"").Append(N(xMm)).Append("\" y=\"").Append(N(baselineYmm))
            .Append("\" font-family=\"").Append(Attr(fontFamily)).Append("\" font-size=\"").Append(N(Mm.PointToMm(sizePt))).Append('"');
        if (bold) _body.Append(" font-weight=\"bold\"");
        if (anchor != SvgTextAnchor.Start)
            _body.Append(" text-anchor=\"").Append(anchor == SvgTextAnchor.Middle ? "middle" : "end").Append('"');
        _body.Append(" fill=\"").Append(fill?.Color ?? "#000000").Append("\">").Append(Escaped(content)).Append("</text>\n");
        ElementCount++;
        return this;
    }

    /// <summary>一段任意已成形 XML（自定义扩展用），原样插入。</summary>
    public SvgBuilder Raw(string xml)
    {
        _body.Append(xml).Append('\n');
        return this;
    }

    public string Build()
    {
        var sb = new StringBuilder(_body.Length + 512);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\n");
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" ");
        // 图层名用 inkscape:label（CDR/Inkscape 都认），前缀必须声明，否则写出去的不是合法 XML
        sb.Append("xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" ");
        sb.Append("version=\"1.1\" width=\"").Append(N(WidthMm)).Append("mm\" height=\"").Append(N(HeightMm)).Append("mm\" ");
        sb.Append("viewBox=\"0 0 ").Append(N(WidthMm)).Append(' ').Append(N(HeightMm)).Append("\">\n");

        if (_metadata.Count > 0)
        {
            sb.Append("<metadata>");
            foreach (var line in _metadata) sb.Append(Escaped(line)).Append('\n');
            sb.Append("</metadata>\n");
        }
        sb.Append(_body);
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    /// <summary>把命令表写成 <c>d</c> 属性（数字全部按 0.### 输出，毫米）。</summary>
    public static string DataOf(IReadOnlyList<SvgPathCommand> commands)
    {
        var sb = new StringBuilder(commands.Count * 16);
        foreach (var cmd in commands)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(cmd.Command);
            foreach (var v in cmd.Args) sb.Append(' ').Append(N(v));
        }
        return sb.ToString();
    }

    private string PaintAttributes(SvgPaint? fill, SvgPaint? stroke, bool evenOdd)
    {
        var sb = new StringBuilder(96);
        if (fill is null) sb.Append("fill=\"none\"");
        else
        {
            sb.Append("fill=\"").Append(Attr(fill.Color)).Append('"');
            if (fill.Opacity < 0.999) sb.Append(" fill-opacity=\"").Append(N(fill.Opacity)).Append('"');
            if (evenOdd) sb.Append(" fill-rule=\"evenodd\"");
        }
        if (stroke is not null)
        {
            sb.Append(" stroke=\"").Append(Attr(stroke.Color)).Append('"')
                .Append(" stroke-width=\"").Append(N(stroke.WidthMm)).Append('"')
                .Append(" stroke-linecap=\"butt\" stroke-linejoin=\"miter\"");
            if (stroke.Opacity < 0.999) sb.Append(" stroke-opacity=\"").Append(N(stroke.Opacity)).Append('"');
            if (stroke.DashMm is { Length: > 0 })
                sb.Append(" stroke-dasharray=\"").Append(string.Join(" ", stroke.DashMm.Select(N))).Append('"');
        }
        return sb.ToString();
    }

    private string Indent() => new string(' ', 2 * (_layers.Count + 1));

    /// <summary>数字输出：0.### 已经够印刷用（0.001mm 分辨率），再多就是文件体积。</summary>
    public static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escaped(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Attr(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}

/// <summary><see cref="SvgWriter"/> 的口径。</summary>
public sealed class SvgWriteOptions
{
    /// <summary>这些 id 的文字节点不写（导入时已被提升成可编辑元素的，要从底图里挖掉，见定案 D6）。</summary>
    public IReadOnlyCollection<string> ExcludedTextIds { get; init; } = Array.Empty<string>();

    public bool IncludeTexts { get; init; } = true;

    public bool IncludeImages { get; init; } = true;

    /// <summary>写进文件的说明行。</summary>
    public string? Comment { get; init; }

    public static SvgWriteOptions Default { get; } = new();
}

/// <summary>
/// <see cref="SvgDocument"/> → SVG 文本。两处在用：
/// ① 导入底稿时把"挖掉可绑文字后的底图"重新落盘（D6）；② 测试里做 <c>Parse → Write → Parse</c> 往返自证。
/// </summary>
public static class SvgWriter
{
    public static string WriteDocument(SvgDocument document, SvgWriteOptions? options = null)
    {
        var opt = options ?? SvgWriteOptions.Default;
        var excluded = new HashSet<string>(opt.ExcludedTextIds, StringComparer.Ordinal);
        var builder = new SvgBuilder(document.WidthMm, document.HeightMm,
            opt.Comment ?? "由 LabelGou 生成的底图（源 SVG 已折算为毫米，1 用户单位 = 1mm）");

        builder.StartLayer("background", "底图");
        foreach (var path in document.Paths)
            builder.Path(path.Commands, path.Fill, path.Stroke, path.EvenOdd);

        if (opt.IncludeImages)
        {
            foreach (var image in document.Images)
                builder.Image(image.Bytes, image.MimeType, image.Bounds.XMm, image.Bounds.YMm, image.Bounds.WidthMm, image.Bounds.HeightMm);
        }

        if (opt.IncludeTexts)
        {
            foreach (var text in document.Texts)
            {
                // XMm 已是按对齐折算好的左端，写回 <text> 时要还原成锚点坐标
                var anchorX = text.Anchor switch
                {
                    SvgTextAnchor.Middle => text.XMm + text.WidthEstimateMm / 2,
                    SvgTextAnchor.End => text.XMm + text.WidthEstimateMm,
                    _ => text.XMm,
                };
                // 剔除判定不只看 Id:合并行(SvgModel.MergedSourceIds)里其余单字的 id 也在里面,
                // 漏剔会把固定文字与可编辑元素各印一遍——合并逻辑给的契约就是「这些 id 都得剔」(第 23 棒)。
                if (text.Id is not null && excluded.Contains(text.Id)) continue;
                if (text.MergedSourceIds is not null && text.MergedSourceIds.Any(excluded.Contains)) continue;
                builder.GlyphText(text, anchorX);
            }
        }
        builder.EndLayer();

        if (document.LayerNames.Count > 0)
            builder.Comment("源文件顶层分组：" + string.Join(" / ", document.LayerNames));
        return builder.Build();
    }

    /// <summary>往返自证用：解析一段 SVG 再写出来。</summary>
    public static string RoundTrip(string svgXml, SvgWriteOptions? options = null)
    {
        var parsed = SvgParser.Parse(svgXml);
        return parsed.HasError ? svgXml : WriteDocument(parsed.Document, options);
    }

    private static void GlyphText(this SvgBuilder builder, SvgText text, double anchorX)
    {
        // 底图里的文字仍然以 <text> 形式保留：它没被提升成可编辑元素，就是要"原样印出来"的固定文字。
        // 是否转曲由 App 层的导出器决定（那里才有字体度量），Core 只做数据搬运。
        builder.Text(text.Content, anchorX, text.BaselineYmm, text.SizePt, text.FontFamily, text.Bold, text.Anchor, text.Fill);
    }
}
