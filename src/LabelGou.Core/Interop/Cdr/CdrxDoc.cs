using System.Text.Json.Serialization;

namespace LabelGou.Core.Interop.Cdr;

/// <summary>
/// cdrx（CDR eXchange）= CorelDRAW 设计的逐对象中间格式。三个来源都落到这一张表上：
/// A 驱动本机 CorelDRAW、B 离线直解 <c>.cdr</c>、C 读 CDR 导出的 SVG；
/// 本软件只认这一份，加第四个来源不用改消费方。
/// 单位一律毫米、坐标一律左上原点 Y 向下（与模板层同一口径）。
/// </summary>
/// <remarks>
/// 字段名与 <c>labegou-CDR</c> 分区那份是同一套约定（那边在 git 仓外，推不上来，故主仓自含一份）。
/// <b>只增不改</b>；读到不认识的版本要拒绝并说清，别半读。
/// </remarks>
public sealed class CdrxDoc
{
    /// <summary>格式版本。高于本程序支持的版本 → <see cref="CdrxReader"/> 拒读。</summary>
    [JsonPropertyName("cdrx")]
    public int FormatVersion { get; set; } = CdrxFormat.Version;

    [JsonPropertyName("source")]
    public CdrxSource Source { get; set; } = new();

    [JsonPropertyName("page")]
    public CdrxSize Page { get; set; } = new();

    [JsonPropertyName("layers")]
    public List<string> Layers { get; set; } = new();

    [JsonPropertyName("objects")]
    public List<CdrxObject> Objects { get; set; } = new();

    /// <summary>对象树摊平（群组递归）。导入要的是扁平列表＋层序。</summary>
    public IEnumerable<CdrxObject> Flattened()
    {
        foreach (var o in Objects)
        {
            yield return o;
            foreach (var c in o.Flattened()) yield return c;
        }
    }
}

public static class CdrxFormat
{
    public const int Version = 1;
}

public sealed class CdrxSource
{
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    /// <summary>cdr-com | cdr-binary | cdr-svg。回归比对按它分组。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("producer")]
    public string Producer { get; set; } = "";

    [JsonPropertyName("corelVersion")]
    public string? CorelVersion { get; set; }

    /// <summary>这份设计整体丢了什么，逐条写明白。缺席≠没问题，只说明这一份没记。</summary>
    [JsonPropertyName("degraded")]
    public List<string> Degraded { get; set; } = new();
}

public sealed class CdrxSize
{
    [JsonPropertyName("w")]
    public double? W { get; set; }

    [JsonPropertyName("h")]
    public double? H { get; set; }
}

/// <summary>左上原点、Y 向下的毫米盒。X/Y 是盒左上角。</summary>
public sealed class CdrxBox
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("w")]
    public double W { get; set; }

    [JsonPropertyName("h")]
    public double H { get; set; }
}

public sealed class CdrxColor
{
    /// <summary>四分量各 0~100。CDR 里填了多少就是多少，绝不由屏幕色反推。</summary>
    [JsonPropertyName("cmyk")]
    public double[]? Cmyk { get; set; }

    [JsonPropertyName("rgb")]
    public string? Rgb { get; set; }

    /// <summary>true＝<see cref="Rgb"/> 是由 Cmyk 减色近似派生的，不是 CDR 报的屏幕色，别拿它当真值。</summary>
    [JsonPropertyName("rgbDerived")]
    public bool RgbDerived { get; set; }

    [JsonPropertyName("spot")]
    public bool Spot { get; set; }

    /// <summary>色名。专色没有等效 CMYK，只能靠它转达。</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Cmyk is null && Rgb is null;
}

/// <summary>cdrx 的元素类型。与模板层 <c>ElementKind</c> 不是一一对应：unknown 与 group 在模板侧没有对应形态。</summary>
public static class CdrxKinds
{
    public const string Text = "text";
    public const string Rect = "rect";
    public const string Ellipse = "ellipse";
    public const string Polygon = "polygon";
    public const string Curve = "curve";
    public const string Line = "line";
    public const string Image = "image";
    public const string Group = "group";
    public const string Unknown = "unknown";
}

public sealed class CdrxObject
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = CdrxKinds.Unknown;

    [JsonPropertyName("layer")]
    public string? Layer { get; set; }

    [JsonPropertyName("z")]
    public int? Z { get; set; }

    [JsonPropertyName("box")]
    public CdrxBox? Box { get; set; }

    /// <summary>
    /// 未旋转的形状框。缺省＝旋转已经烤进 <see cref="Box"/>，
    /// 消费方拿了 <see cref="Rot"/> 再转一次就是把旋转算两遍。
    /// </summary>
    [JsonPropertyName("uBox")]
    public CdrxBox? UnrotatedBox { get; set; }

    [JsonPropertyName("rot")]
    public double? Rot { get; set; }

    /// <summary>
    /// Corel 施加在这个形状上的横向拉伸（1＝没拉伸）。金沐那块三行文字实测是 <c>0.821798</c>——
    /// 不带上它，导入进来就是 100% 宽，字身比例与 Corel 里完全不一样（用户 2026-09-19 圈的"拉伸比例不同"）。
    /// 与 <see cref="Rot"/> 同源于 <c>trfd</c> 那条矩阵。缺字段＝这条通道没读到。
    /// </summary>
    [JsonPropertyName("sx")]
    public double? ScaleX { get; set; }

    /// <summary>纵向拉伸，同上。见 <see cref="ScaleX"/>。</summary>
    [JsonPropertyName("sy")]
    public double? ScaleY { get; set; }

    /// <summary>false＝隐形对象（有盒没墨）。缺省＝true＝可见。</summary>
    [JsonPropertyName("visible")]
    public bool? Visible { get; set; }

    [JsonPropertyName("ink")]
    public CdrxColor? Ink { get; set; }

    [JsonPropertyName("stroke")]
    public CdrxStroke? Stroke { get; set; }

    [JsonPropertyName("text")]
    public CdrxText? Text { get; set; }

    [JsonPropertyName("geom")]
    public CdrxGeom? Geom { get; set; }

    [JsonPropertyName("curve")]
    public CdrxCurve? Curve { get; set; }

    [JsonPropertyName("image")]
    public CdrxImage? Image { get; set; }

    /// <summary>这一条为什么是现在这样（降级、未支持、取不到）。缺席＝没说，不等于没问题。</summary>
    [JsonPropertyName("notes")]
    public List<string>? Notes { get; set; }

    [JsonPropertyName("children")]
    public List<CdrxObject>? Children { get; set; }

    [JsonIgnore]
    public bool IsVisible => Visible is not false;

    public IEnumerable<CdrxObject> Flattened()
    {
        if (Children is null) yield break;
        foreach (var c in Children)
        {
            yield return c;
            foreach (var g in c.Flattened()) yield return g;
        }
    }
}

public sealed class CdrxStroke
{
    [JsonPropertyName("wMm")]
    public double? WidthMm { get; set; }

    [JsonPropertyName("color")]
    public CdrxColor? Color { get; set; }

    [JsonPropertyName("on")]
    public bool? On { get; set; }
}

public sealed class CdrxText
{
    string _contents = "";

    /// <summary>换行一律 <c>\n</c>；来源若报裸 <c>\r</c>，在读入这一处归一，下游不许各自 Split。</summary>
    [JsonPropertyName("contents")]
    public string Contents
    {
        get => _contents;
        set => _contents = NormalizeNewlines(value);
    }

    public static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    [JsonIgnore]
    public string[] Lines => Contents.Split('\n');

    [JsonPropertyName("artistic")]
    public bool? Artistic { get; set; }

    [JsonPropertyName("font")]
    public CdrxFont? Font { get; set; }
}

public sealed class CdrxFont
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("sizePt")]
    public double? SizePt { get; set; }

    [JsonPropertyName("bold")]
    public bool? Bold { get; set; }

    [JsonPropertyName("italic")]
    public bool? Italic { get; set; }
}

/// <summary>参数化形状的额外事实：矩形圆角、多边形边数。丢了就只能画成折线轮廓。</summary>
public sealed class CdrxGeom
{
    [JsonPropertyName("cornerWMm")]
    public double? CornerWMm { get; set; }

    [JsonPropertyName("cornerHMm")]
    public double? CornerHMm { get; set; }

    [JsonPropertyName("sides")]
    public int? Sides { get; set; }
}

public sealed class CdrxCurve
{
    [JsonPropertyName("closed")]
    public bool? Closed { get; set; }

    [JsonPropertyName("subpaths")]
    public List<CdrxSubpath>? Subpaths { get; set; }
}

public sealed class CdrxSubpath
{
    [JsonPropertyName("closed")]
    public bool? Closed { get; set; }

    [JsonPropertyName("nodes")]
    public List<CdrxNode> Nodes { get; set; } = new();
}

public sealed class CdrxNode
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    /// <summary>smooth | corner | sym</summary>
    [JsonPropertyName("t")]
    public string? Type { get; set; }

    [JsonPropertyName("c1x")]
    public double? C1X { get; set; }

    [JsonPropertyName("c1y")]
    public double? C1Y { get; set; }

    [JsonPropertyName("c2x")]
    public double? C2X { get; set; }

    [JsonPropertyName("c2y")]
    public double? C2Y { get; set; }
}

public sealed class CdrxImage
{
    /// <summary>相对 <c>*.cdrx.json</c> 所在目录的路径，如 <c>objects/0003.png</c>。</summary>
    [JsonPropertyName("file")]
    public string File { get; set; } = "";

    [JsonPropertyName("px")]
    public int? PixelW { get; set; }

    [JsonPropertyName("py")]
    public int? PixelH { get; set; }
}
