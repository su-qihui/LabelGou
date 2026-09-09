using System.Globalization;
using System.Text;
using System.Xml.Linq;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.Core.Interop.Svg;

/// <summary>一次解析的产物：中性文档 + 问题清单（沿用 <see cref="TemplateIssue"/>，不另建平行体系）。</summary>
public sealed record SvgParseResult(SvgDocument Document, IReadOnlyList<TemplateIssue> Issues)
{
    public bool HasError => Issues.Any(i => i.Severity == IssueLevel.Error);

    public IReadOnlyList<string> ErrorMessages => Issues.ErrorMessages();
}

/// <summary>解析口径。</summary>
public sealed class SvgParseOptions
{
    /// <summary>节点上限：底稿大到这个程度基本是误把一个画册文件当单枚唛头传进来了。</summary>
    public int MaxNodes { get; init; } = 20000;

    /// <summary>画布尺寸写不出来时，一个用户单位按多少毫米算（默认 1px 口径）。</summary>
    public double FallbackUserUnitMm { get; init; } = SvgLength.MmPerPixel;

    /// <summary>只要几何不要文字（生成"纯底图"时用）。</summary>
    public bool SkipTexts { get; init; }

    /// <summary>
    /// 把被逐字拆开的文字拼回整行（<see cref="SvgTextLineJoiner"/>），默认开。
    /// <para>关掉它是给往返测试留的口：要验“源文件怎么写进来就怎么写出去”时，不能被我们的合并改变元素数。</para>
    /// </summary>
    public bool JoinTextLines { get; init; } = true;

    /// <summary>元素数超过这个值就停止收集并报错（比 MaxNodes 早，给用户看得懂的话）。</summary>
    public int WarnElementCount { get; init; } = 4000;

    public static SvgParseOptions Default { get; } = new();
}

/// <summary>
/// SVG 底稿读取器（C 类模板的来源）：<strong>把 SVG 折算成毫米 + 磅</strong>，一次换算、一次定坐标。
/// <para>
/// 为什么读 SVG 而不是 .cdr：<c>.cdr</c> 是 CorelDRAW 专有二进制格式，矢量对象层零依赖读不了
/// （证据见 <c>labelgou-word\specs\2026-09-07-m5-cdr-interchange-design.md</c> §1.2）；
/// 而 CDR 自己就能"文件→导出为 SVG"。所以本读取器面向的是 CDR/Illustrator/Inkscape 导出的交换件。
/// </para>
/// <para>
/// <strong>曲线是完整支持的</strong>（M5 定案甲）：<c>C/S/Q/T/A</c> 全部归一成绝对的三次贝塞尔 <c>C</c>，
/// 圆弧按 SVG 规范 E.6 转贝塞尔，二次按 2/3 规则升三次——因为 CDR 底稿的圆角框、菱形危险品标志、Logo
/// 全是贝塞尔，降级成包围盒等于底图不可用。
/// </para>
/// <para>
/// 所有几何（rect/circle/ellipse/line/polyline/polygon/path）最后都收敛成一个 <see cref="SvgPath"/>，
/// 命令只剩 <c>M/L/C/Z</c>；坐标已在解析时乘完 viewBox 系数与累加 transform，出树即毫米。
/// </para>
/// </summary>
public static class SvgParser
{
    public static SvgParseResult ParseFile(string path, SvgParseOptions? options = null)
    {
        try
        {
            return Parse(File.ReadAllText(path), options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            var doc = new SvgDocument();
            return new SvgParseResult(doc, new[]
            {
                new TemplateIssue(IssueLevel.Error, $"底稿文件打不开：{ex.Message}"),
            });
        }
    }

    public static SvgParseResult Parse(string xml, SvgParseOptions? options = null)
    {
        var opt = options ?? SvgParseOptions.Default;
        var doc = new SvgDocument();
        var issues = new List<TemplateIssue>();

        XDocument parsed;
        try
        {
            // XDocument.Parse 没有带 XmlReaderSettings 的重载，要进 XmlReader 这条路
            using var reader = System.Xml.XmlReader.Create(new StringReader(xml ?? string.Empty), new System.Xml.XmlReaderSettings
            {
                // 真样本教训：CorelDRAW/Illustrator 导出的 SVG 开头就带
                // <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/.../svg11.dtd">，
                // 用 DtdProcessing.Prohibit 会直接抛异常 → 每一张真底稿都被当成“读不了”拒收。
                // Ignore 才是既收得下、又不去网上取 DTD 的选项（XmlResolver=null 封死外部实体，
                // MaxCharactersFromEntities 挡住实体爆炸）。
                DtdProcessing = System.Xml.DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = 64L * 1024 * 1024,
                MaxCharactersFromEntities = 1_000_000L,
            });
            parsed = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error, $"这不是一份能读的 SVG：{ex.Message}"));
            return new SvgParseResult(doc, issues);
        }

        if (parsed.Root is null || !parsed.Root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new TemplateIssue(IssueLevel.Error, "文件根节点不是 <svg>，不是 SVG 底稿。"));
            return new SvgParseResult(doc, issues);
        }

        new Job(doc, issues, opt).Run(parsed.Root!);

        // CorelDRAW/Illustrator 导出的 SVG 常把一行拆成单字（本机真样本：BOLAROM = 7 个 <text>）。
        // 不拼回来，字段识别、底图剔除、导入预览全部落空，所以这一步在解析收尾就做，不留给调用方选。
        var joined = opt.JoinTextLines && !opt.SkipTexts
            ? SvgTextLineJoiner.Join(doc)
            : default;
        if (joined.After < joined.Before)
            issues.Add(new TemplateIssue(IssueLevel.Info, joined.Describe()));

        return new SvgParseResult(doc, issues);
    }

    /// <summary>
    /// 宽度估算：宽字符（汉字、假名、CJK 标点、全角形式）约一个全角，拉丁与数字约 0.55 个。
    /// 只用于给导入的文字一个初始框，
    /// <strong>不参与任何印刷尺寸决定</strong>（真度量在渲染端由 <c>FormattedText</c> 完成）。
    /// <para>但它是 <see cref="SvgTextLineJoiner"/> 算字间距的依据：把全角冒号当成半角标点，
    /// 右端就少算半个字，行内间距被夸大，真样本里“冒号与它后面的值”就会被判成两栏。</para>
    /// </summary>
    public static double EstimateWidthMm(string text, double sizeMm)
    {
        var units = 0.0;
        foreach (var c in text)
        {
            if (IsWide(c)) units += 1.0;                            // 宽字符：一个算一个全角
            else if (c is >= 'A' and <= 'Z') units += 0.72;
            else if (c is >= 'a' and <= 'z') units += 0.55;
            else if (c is >= '0' and <= '9') units += 0.56;
            else if (c == ' ') units += 0.28;
            else units += 0.45;
        }
        return Math.Max(sizeMm * 0.3, units * sizeMm);
    }

    /// <summary>宽字符判定：只认这几段，不做完整 East Asian Width 表（那会把表搬进仓库，不值）。</summary>
    private static bool IsWide(char c) =>
        c is >= '\u2E80' and <= '\u9FFF'   // CJK 部首、汉字、假名
        or >= '\u3000' and <= '\u303F'     // CJK 符号与标点（、。〃々〆〇）与全角空格
        or >= '\uFF00' and <= '\uFF60'     // 全角形式（Ａ Ｚ ！？ ，： 等）
        or >= '\uFE30' and <= '\uFE4F';    // CJK 兼容形式（竖排括号等）

    /// <summary>一次解析的工作状态（矩阵链、样式链、id 表、去重后的告警）。</summary>
    private sealed class Job
    {
        private readonly SvgDocument _doc;
        private readonly List<TemplateIssue> _issues;
        private readonly SvgParseOptions _opt;
        private readonly Dictionary<string, XElement> _byId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _warnedOnce = new(StringComparer.OrdinalIgnoreCase);
        private readonly XName _hrefAttr = XName.Get("href", "http://www.w3.org/1999/xlink");
        private double _ux = 1, _uy = 1;
        private bool _truncated;

        public Job(SvgDocument doc, List<TemplateIssue> issues, SvgParseOptions opt)
        {
            _doc = doc;
            _issues = issues;
            _opt = opt;
        }

        public void Run(XElement root)
        {
            foreach (var e in root.DescendantsAndSelf())
            {
                var id = (string?)e.Attribute("id");
                if (!string.IsNullOrWhiteSpace(id) && !_byId.ContainsKey(id)) _byId[id] = e;
            }

            ResolveCanvas(root);
            var sheet = SvgStyleSheet.Collect(root);

            // viewBox 的平移与"用户单位→毫米"的比例，统一塞进根矩阵：先减原点再乘比例
            var origin = SvgMatrix.Translate(-_originX, -_originY);
            var scale = SvgMatrix.Scale(_ux, _uy);
            Walk(root, origin.Then(scale), sheet, SvgStyle.Default, depth: 0, insideUse: 0);

            if (_truncated)
                _issues.Add(new TemplateIssue(IssueLevel.Error,
                    $"底稿节点数超过 {_opt.MaxNodes}，已停止读取。请确认传进来的是单枚唛头而不是一本画册。"));
            else if (_doc.ElementCount > _opt.WarnElementCount)
                _issues.Add(new TemplateIssue(IssueLevel.Warning,
                    $"底稿有 {_doc.ElementCount} 个元素，属于很重的文件；预览会慢，导入后只有被提升的文字参与编辑。"));
        }

        // ---- 画布尺寸与"1 用户单位 = 多少毫米" ----

        private double _originX, _originY;

        private void ResolveCanvas(XElement root)
        {
            var viewBox = (string?)root.Attribute("viewBox");
            double? vbX = null, vbY = null, vbW = null, vbH = null;
            if (!string.IsNullOrWhiteSpace(viewBox))
            {
                var nums = SvgMatrix.ParseNumberList(viewBox);
                if (nums.Count >= 4)
                {
                    vbX = nums[0];
                    vbY = nums[1];
                    vbW = nums[2];
                    vbH = nums[3];
                }
                else
                {
                    WarnOnce("viewBox", $"viewBox「{viewBox}」不是四个数，已忽略。");
                }
            }

            var widthMm = LengthInMm((string?)root.Attribute("width"), _opt.FallbackUserUnitMm);
            var heightMm = LengthInMm((string?)root.Attribute("height"), _opt.FallbackUserUnitMm);

            if (vbW is > 0 && vbH is > 0)
            {
                _originX = vbX!.Value;
                _originY = vbY!.Value;
                // 有 viewBox 时：宽度声明决定比例；只有一边就用那边；两边都没有就按 px 口径
                if (widthMm is > 0 && heightMm is > 0)
                {
                    // preserveAspectRatio="…slice" 会裁掉一边，meet 会留白；两种都按各自轴取比例，
                    // 这是比"硬猜一个统一系数"更接近人眼的做法
                    _ux = widthMm.Value / vbW.Value;
                    _uy = heightMm.Value / vbH.Value;
                    if (string.Equals((string?)root.Attribute("preserveAspectRatio"), "none", StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(_ux - _uy) > 1e-9)
                    {
                        WarnOnce("preserveAspectRatio", "底稿的 viewBox 与宽高比例不一致（preserveAspectRatio=\"none\"），已按各自轴分别缩放，圆形可能变成椭圆。");
                    }
                    _doc.WidthMm = widthMm.Value;
                    _doc.HeightMm = heightMm.Value;
                }
                else if (widthMm is > 0)
                {
                    _ux = _uy = widthMm.Value / vbW.Value;
                    _doc.WidthMm = widthMm.Value;
                    _doc.HeightMm = vbH.Value * _uy;
                }
                else if (heightMm is > 0)
                {
                    _ux = _uy = heightMm.Value / vbH.Value;
                    _doc.WidthMm = vbW.Value * _ux;
                    _doc.HeightMm = heightMm.Value;
                }
                else
                {
                    _ux = _uy = _opt.FallbackUserUnitMm;
                    _doc.WidthMm = vbW.Value * _ux;
                    _doc.HeightMm = vbH.Value * _uy;
                    WarnOnce("size", "底稿只有 viewBox 没写宽高，已按 1 用户单位 = 1px（0.2646mm）算，尺寸可能与实际不符。");
                }
            }
            else
            {
                // 无 viewBox 时用户单位就是 px，但 width 本身可以写成 mm/in，坐标仍按 px 算
                _ux = _uy = _opt.FallbackUserUnitMm;
                _doc.WidthMm = widthMm is > 0 ? widthMm.Value : _opt.FallbackUserUnitMm * 100;
                _doc.HeightMm = heightMm is > 0 ? heightMm.Value : _opt.FallbackUserUnitMm * 100;
                if (widthMm is not > 0 || heightMm is not > 0)
                    WarnOnce("size", "底稿没写 width/height 也没写 viewBox，已按 100px 见方兜底，导入后请手工核对标签尺寸。");
            }

            _doc.UserUnitMm = _ux;
        }

        private static double? LengthInMm(string? raw, double userUnitMm)
        {
            if (SvgLength.Parse(raw) is not { } len) return null;
            var mm = len.ToMillimetres(userUnitMm);
            return double.IsNaN(mm) ? null : mm;
        }

        // ---- 树遍历 ----

        private void Walk(XElement element, SvgMatrix m, SvgStyleSheet sheet, SvgStyle style, int depth, int insideUse)
        {
            if (_truncated) return;
            if (depth > 64)
            {
                _truncated = true;
                WarnOnce("depth", "底稿嵌套超过 64 层，已停止向下读。");
                return;
            }
            if (_doc.NodeCount++ >= _opt.MaxNodes)
            {
                _truncated = true;
                return;
            }

            var own = m;
            var transformText = (string?)element.Attribute("transform");
            if (!string.IsNullOrWhiteSpace(transformText))
            {
                var parsed = SvgMatrix.Parse(transformText, out var unparsed);
                if (parsed is not null) own = parsed.Value.Then(own); // 自己的变换先作用，再是父级
                if (unparsed is not null)
                    WarnOnce("transform:" + unparsed, $"有一段 transform 没认出来（{unparsed}），该元素的位置可能与底稿有偏差。");
            }

            var ownStyle = style.Overlay(element, sheet, _ux);
            if (!ownStyle.Displayed) return;

            var localName = element.Name.LocalName;
            var name = localName.ToLowerInvariant();
            switch (name)
            {
                case "svg":
                case "g":
                case "a":
                case "switch":
                    if (name == "switch") WarnOnce("switch", "底稿里有 <switch>（条件分支），已当成普通分组全部画出。");
                    if (depth == 1 && name == "g") NoteLayer(element);
                    foreach (var child in element.Elements()) Walk(child, own, sheet, ownStyle, depth + 1, insideUse);
                    return;

                case "defs":
                case "symbol":
                case "clippath":
                case "mask":
                case "filter":
                case "lineargradient":
                case "radialgradient":
                case "pattern":
                case "marker":
                case "style":
                case "title":
                case "desc":
                case "metadata":
                case "namedview":
                    // defs/symbol/metadata 一类只承载定义或编辑器私货，静默跳过；
                    // 渐变与图案在真正被引用时由 SvgColor/下面的 fill 路径降级并告警
                    if (name is "clippath" or "mask" or "filter" or "marker")
                        WarnOnce(name, $"<{localName}> 不支持，被它裁出来的形状会整份显示（可能多出本该被遮住的部分）。");
                    if (name is "pattern" or "lineargradient" or "radialgradient")
                        WarnOnce(name, $"底稿用到 <{localName}>，渐变/图案填充已降级为纯色。");
                    return;

                case "use":
                    EmitUse(element, own, sheet, ownStyle, depth, insideUse);
                    return;

                case "rect":
                case "circle":
                case "ellipse":
                case "line":
                case "polyline":
                case "polygon":
                case "path":
                    EmitPath(element, name, own, ownStyle);
                    return;

                case "text":
                    if (!_opt.SkipTexts) EmitText(element, own, sheet, ownStyle);
                    return;

                case "image":
                    EmitImage(element, own, ownStyle);
                    return;

                default:
                    WarnOnce(name, $"不认的标签 <{localName}>，已跳过。");
                    return;
            }
        }

        private void NoteLayer(XElement group)
        {
            // Inkscape/CDR 导出的顶层分组带 id 或 inkscape:label，导出时复用成图层名能省用户不少整理工夫
            var picked = (string?)group.Attribute(XName.Get("label", "http://www.inkscape.org/namespaces/inkscape"))
                         ?? (string?)group.Attribute("id");
            if (!string.IsNullOrWhiteSpace(picked) && _doc.LayerNames.Count < 40) _doc.LayerNames.Add(picked.Trim());
        }

        // ---- 几何 ----

        private void EmitPath(XElement element, string kind, SvgMatrix m, SvgStyle style)
        {
            List<SvgPathCommand> user; // 用户单位下的绝对命令
            switch (kind)
            {
                case "rect": user = RectCommands(element); break;
                case "circle": user = EllipseCommands(element, "cx", "cy", "r", "r"); break;
                case "ellipse": user = EllipseCommands(element, "cx", "cy", "rx", "ry"); break;
                case "line": user = LineCommands(element); break;
                case "polyline":
                case "polygon": user = PolyCommands(element, closed: kind == "polygon"); break;
                default: user = PathCommands(element); break;
            }

            if (user.Count == 0)
            {
                WarnOnce(kind, $"<{kind}> 里没有可用几何（可能是宽高为 0），已跳过。");
                return;
            }
            if (m.HasFlip) WarnOnce("flip", "底稿里有镜像变换（负方向），形状方向可能与 CDR 里看到的相反，导入后请核对。");

            var absolute = new List<SvgPathCommand>(user.Count);
            var bounds = SvgBounds.Empty;
            foreach (var cmd in user)
            {
                if (cmd.Command == 'Z')
                {
                    absolute.Add(Cmd('Z'));
                    continue;
                }
                var args = new double[cmd.Args.Count];
                for (var i = 0; i + 1 < args.Length; i += 2)
                {
                    var (x, y) = m.Map(cmd.Args[i], cmd.Args[i + 1]);
                    args[i] = x;
                    args[i + 1] = y;
                    bounds = bounds.Add(x, y);
                }
                absolute.Add(new SvgPathCommand(cmd.Command, args));
            }

            var widthUser = Math.Max(0.01, style.StrokeWidthUser);
            var fill = BuildPaint(style.Fill, style.FillOpacity, dash: null, strokeWidthUser: widthUser, scale: m.StrokeScale);
            var stroke = BuildPaint(style.Stroke, style.StrokeOpacity, dash: style.Dash, strokeWidthUser: widthUser, scale: m.StrokeScale);
            if (fill is null && stroke is null)
            {
                WarnOnce("nopaint", $"<{kind}> 既没填充也没描边，画不出东西（多半是 fill:none 且没给 stroke），已跳过。");
                return;
            }

            _doc.Add(new SvgPath
            {
                SourceTag = kind,
                Id = (string?)element.Attribute("id"),
                Commands = absolute,
                Fill = fill,
                Stroke = stroke,
                EvenOdd = style.EvenOdd,
                Bounds = bounds,
            });
        }

        /// <summary>
        /// 把样式上的一笔（颜色串 + 不透明度 + 可选虚线）归一成 <see cref="SvgPaint"/>。
        /// <para>虚线阵与线宽都是<strong>用户单位</strong>，靠 <paramref name="scale"/>（含 viewBox 系数与累加 transform）一次性折成毫米，
        /// 不能拿线宽去乘虚线——那是两个量纲。</para>
        /// </summary>
        private SvgPaint? BuildPaint(string? raw, double opacity, string? dash, double strokeWidthUser, double scale)
        {
            var color = SvgColor.Normalize(raw, out var degraded);
            if (color is null) return null;
            if (degraded is not null) WarnOnce("paint:" + degraded, degraded);
            if (opacity <= 0.001) return null;
            double[]? dashMm = null;
            if (!string.IsNullOrWhiteSpace(dash) && dash.Trim() != "none")
            {
                var nums = SvgMatrix.ParseNumberList(dash).Select(v => v * scale).ToList();
                nums.RemoveAll(double.IsNaN);
                if (nums.Count > 0) dashMm = nums.ToArray();
                else WarnOnce("dash", "stroke-dasharray 没认出数，这条线按实线画。");
            }
            return new SvgPaint
            {
                Color = color,
                Opacity = Math.Clamp(opacity, 0, 1),
                WidthMm = Math.Max(0.02, strokeWidthUser * scale),
                DashMm = dashMm,
                Degraded = degraded,
            };
        }

        private List<SvgPathCommand> RectCommands(XElement e)
        {
            var x = Num(e, "x");
            var y = Num(e, "y");
            var w = Num(e, "width");
            var h = Num(e, "height");
            if (w <= 0 || h <= 0)
            {
                WarnOnce("rect0", "有 <rect> 的宽或高是 0，已跳过。");
                return new List<SvgPathCommand>();
            }
            var rx = NumOpt(e, "rx");
            var ry = NumOpt(e, "ry");
            if (rx is null && ry is null) return BoxCommands(x, y, w, h, closed: true);
            rx ??= ry!.Value;
            ry ??= rx!.Value;
            rx = Math.Min(rx.Value, w / 2);
            ry = Math.Min(ry.Value, h / 2);
            if (rx <= 0 || ry <= 0) return BoxCommands(x, y, w, h, closed: true);
            return RoundedRectCommands(x, y, w, h, rx.Value, ry.Value);
        }

        private static List<SvgPathCommand> BoxCommands(double x, double y, double w, double h, bool closed)
        {
            var list = new List<SvgPathCommand>
            {
                Cmd('M', x, y),
                Cmd('L', x + w, y),
                Cmd('L', x + w, y + h),
                Cmd('L', x, y + h),
            };
            if (closed) list.Add(Cmd('Z'));
            return list;
        }

        /// <summary>圆角矩形：四个角用 1/4 圆弧的贝塞尔近似（k = 0.5522847）。</summary>
        private static List<SvgPathCommand> RoundedRectCommands(double x, double y, double w, double h, double rx, double ry)
        {
            const double k = 0.5522847498;
            var right = x + w;
            var bottom = y + h;
            var kx = rx * k;
            var ky = ry * k;
            return new List<SvgPathCommand>
            {
                Cmd('M', x + rx, y),
                Cmd('L', right - rx, y),
                Cubic(right - rx + kx, y, right, y + ry - ky, right, y + ry),
                Cmd('L', right, bottom - ry),
                Cubic(right, bottom - ry + ky, right - rx + kx, bottom, right - rx, bottom),
                Cmd('L', x + rx, bottom),
                Cubic(x + rx - kx, bottom, x, bottom - ry + ky, x, bottom - ry),
                Cmd('L', x, y + ry),
                Cubic(x, y + ry - ky, x + rx - kx, y, x + rx, y),
                Cmd('Z'),
            };
        }

        private List<SvgPathCommand> EllipseCommands(XElement e, string cxName, string cyName, string rxName, string ryName)
        {
            var cx = Num(e, cxName);
            var cy = Num(e, cyName);
            var rx = Num(e, rxName);
            var ry = rxName == ryName ? rx : Num(e, ryName);
            if (rx <= 0 || ry <= 0) return new List<SvgPathCommand>();

            const double k = 0.5522847498;
            return new List<SvgPathCommand>
            {
                Cmd('M', cx - rx, cy),
                Cubic(cx - rx, cy - ry * k, cx - rx * k, cy - ry, cx, cy - ry),
                Cubic(cx + rx * k, cy - ry, cx + rx, cy - ry * k, cx + rx, cy),
                Cubic(cx + rx, cy + ry * k, cx + rx * k, cy + ry, cx, cy + ry),
                Cubic(cx - rx * k, cy + ry, cx - rx, cy + ry * k, cx - rx, cy),
                Cmd('Z'),
            };
        }

        private List<SvgPathCommand> LineCommands(XElement e)
        {
            var list = new List<SvgPathCommand>
            {
                Cmd('M', Num(e, "x1"), Num(e, "y1")),
                Cmd('L', Num(e, "x2"), Num(e, "y2")),
            };
            return list;
        }

        private List<SvgPathCommand> PolyCommands(XElement e, bool closed)
        {
            var nums = SvgMatrix.ParseNumberList((string?)e.Attribute("points"));
            var list = new List<SvgPathCommand>();
            for (var i = 0; i + 1 < nums.Count; i += 2)
                list.Add(Cmd(list.Count == 0 ? 'M' : 'L', nums[i], nums[i + 1]));
            if (list.Count > 0 && closed) list.Add(Cmd('Z'));
            if (nums.Count % 2 != 0) WarnOnce("points", "有一处 points 的坐标数是奇数，最后落单的那个已丢弃。");
            return list;
        }

        /// <summary>
        /// <c>path@d</c> → 绝对 M/L/C/Z。H/V 折成 L，S/T 用上一个控制点反射补齐，Q 升三次，A 转三次贝塞尔。
        /// <para>命令的重复（如 <c>l 20 20 20 20</c>）按规范用前一个命令类型续读。</para>
        /// </summary>
        private List<SvgPathCommand> PathCommands(XElement e)
        {
            var tokens = SvgPathTokenizer.Tokenize((string?)e.Attribute("d"));
            var commands = new List<SvgPathCommand>();
            var nums = new List<double>();

            var cmd = '\0';
            var prevCmd = '\0';
            var x = 0d;
            var y = 0d;
            var startX = 0d;
            var startY = 0d;
            var lastCubic = (X: double.NaN, Y: double.NaN);
            var lastQuad = (X: double.NaN, Y: double.NaN);

            void Flush()
            {
                if (cmd == '\0') return;
                Emit(cmd, nums);
                nums.Clear();
            }

            void Emit(char c, List<double> args)
            {
                var rel = char.IsLower(c);
                var up = char.ToUpperInvariant(c);
                var width = up switch
                {
                    'M' or 'L' or 'T' => 2,
                    'H' or 'V' => 1,
                    'C' => 6,
                    'S' or 'Q' => 4,
                    'A' => 7,
                    'Z' => 0,
                    _ => -1,
                };
                if (width < 0)
                {
                    WarnOnce("pathcmd:" + up, $"路径命令「{c}」不是 SVG 的命令，已忽略该命令。");
                    prevCmd = c;
                    return;
                }
                if (up == 'Z')
                {
                    commands.Add(Cmd('Z'));
                    x = startX;
                    y = startY;
                    lastCubic = lastQuad = (double.NaN, double.NaN);
                    prevCmd = c;
                    return;
                }

                var consumed = 0;
                var lastUsed = '\0';
                do
                {
                    if (args.Count - consumed < width)
                    {
                        WarnOnce("pathlen", "路径 d 里有命令的参数个数不够，多余部分已忽略（多半是文件被截断或写坏了）。");
                        break;
                    }
                    var a = new double[width];
                    for (var i = 0; i < width; i++) a[i] = args[consumed + i];
                    consumed += width;

                    // 一个 M/m 后面多出来的坐标对，按规范是隐式的 L/l
                    var use = up == 'M' && consumed > width ? 'L' : up;
                    lastUsed = use;

                    var nx = x;
                    var ny = y;
                    switch (use)
                    {
                        case 'M':
                            nx = rel ? x + a[0] : a[0];
                            ny = rel ? y + a[1] : a[1];
                            commands.Add(Cmd('M', nx, ny));
                            startX = nx;
                            startY = ny;
                            break;
                        case 'L':
                            nx = rel ? x + a[0] : a[0];
                            ny = rel ? y + a[1] : a[1];
                            commands.Add(Cmd('L', nx, ny));
                            break;
                        case 'H':
                            nx = rel ? x + a[0] : a[0];
                            commands.Add(Cmd('L', nx, y));
                            break;
                        case 'V':
                            ny = rel ? y + a[0] : a[0];
                            commands.Add(Cmd('L', x, ny));
                            break;
                        case 'C':
                        {
                            var c1x = rel ? x + a[0] : a[0];
                            var c1y = rel ? y + a[1] : a[1];
                            var c2x = rel ? x + a[2] : a[2];
                            var c2y = rel ? y + a[3] : a[3];
                            nx = rel ? x + a[4] : a[4];
                            ny = rel ? y + a[5] : a[5];
                            commands.Add(Cubic(c1x, c1y, c2x, c2y, nx, ny));
                            lastCubic = (c2x, c2y);
                            lastQuad = (double.NaN, double.NaN);
                            break;
                        }
                        case 'S':
                        {
                            // 反射上一个三次曲线的第二个控制点；上一个不是 C/S 时退化为当前点
                            var (r1x, r1y) = char.ToUpperInvariant(prevCmd) is 'C' or 'S' && !double.IsNaN(lastCubic.X)
                                ? (2 * x - lastCubic.X, 2 * y - lastCubic.Y)
                                : (x, y);
                            var c2x = rel ? x + a[0] : a[0];
                            var c2y = rel ? y + a[1] : a[1];
                            nx = rel ? x + a[2] : a[2];
                            ny = rel ? y + a[3] : a[3];
                            commands.Add(Cubic(r1x, r1y, c2x, c2y, nx, ny));
                            lastCubic = (c2x, c2y);
                            lastQuad = (double.NaN, double.NaN);
                            break;
                        }
                        case 'Q':
                        {
                            var qx = rel ? x + a[0] : a[0];
                            var qy = rel ? y + a[1] : a[1];
                            nx = rel ? x + a[2] : a[2];
                            ny = rel ? y + a[3] : a[3];
                            // 二次升三次：c1 = p0 + 2/3(q-p0)，c2 = p1 + 2/3(q-p1)
                            commands.Add(Cubic(x + 2.0 / 3.0 * (qx - x), y + 2.0 / 3.0 * (qy - y),
                                nx + 2.0 / 3.0 * (qx - nx), ny + 2.0 / 3.0 * (qy - ny), nx, ny));
                            lastQuad = (qx, qy);
                            lastCubic = (double.NaN, double.NaN);
                            break;
                        }
                        case 'T':
                        {
                            var (qx, qy) = char.ToUpperInvariant(prevCmd) is 'Q' or 'T' && !double.IsNaN(lastQuad.X)
                                ? (2 * x - lastQuad.X, 2 * y - lastQuad.Y)
                                : (x, y);
                            nx = rel ? x + a[0] : a[0];
                            ny = rel ? y + a[1] : a[1];
                            commands.Add(Cubic(x + 2.0 / 3.0 * (qx - x), y + 2.0 / 3.0 * (qy - y),
                                nx + 2.0 / 3.0 * (qx - nx), ny + 2.0 / 3.0 * (qy - ny), nx, ny));
                            lastQuad = (qx, qy);
                            lastCubic = (double.NaN, double.NaN);
                            break;
                        }
                        case 'A':
                        {
                            var rx = Math.Abs(a[0]);
                            var ry = Math.Abs(a[1]);
                            var rot = a[2];
                            var large = a[3] != 0;
                            var sweep = a[4] != 0;
                            nx = rel ? x + a[5] : a[5];
                            ny = rel ? y + a[6] : a[6];
                            if (rx <= 0 || ry <= 0)
                            {
                                commands.Add(Cmd('L', nx, ny));
                            }
                            else if (Math.Abs(nx - x) < 1e-12 && Math.Abs(ny - y) < 1e-12)
                            {
                                // 起终点重合：规范说整段不画
                            }
                            else
                            {
                                foreach (var arc in SvgArc.ToBeziers(x, y, rx, ry, rot, large, sweep, nx, ny))
                                    commands.Add(Cubic(arc.C1X, arc.C1Y, arc.C2X, arc.C2Y, arc.X, arc.Y));
                            }
                            lastCubic = lastQuad = (double.NaN, double.NaN);
                            break;
                        }
                    }
                    x = nx;
                    y = ny;
                }
                while (consumed < args.Count);

                prevCmd = lastUsed == '\0' ? c : lastUsed;
            }

            foreach (var token in tokens)
            {
                if (token.IsCommand)
                {
                    Flush();
                    cmd = token.Command;
                }
                else
                {
                    if (cmd == '\0')
                    {
                        // 有数字没命令：SVG 允许开头隐式 moveto，但更常见的是文件写坏了
                        WarnOnce("pathhead", "路径 d 开头就是数字（没有命令字母），已按 M 起头处理。");
                        cmd = 'M';
                    }
                    nums.Add(token.Number);
                }
            }
            Flush();
            return commands;
        }


        // ---- 文字 ----

        private void EmitText(XElement element, SvgMatrix m, SvgStyleSheet sheet, SvgStyle style)
        {
            var xAttr = NumOpt(element, "x");
            var yAttr = NumOpt(element, "y");
            var rotate = (string?)element.Attribute("rotate");
            if (!string.IsNullOrWhiteSpace(rotate) && SvgMatrix.ParseNumberList(rotate).Count > 0)
                WarnOnce("rotate", "底稿里有文字旋转（rotate 属性），已按不旋转落位，导入后请在编辑器里核对方向。");

            var segments = CollectTextSegments(element, sheet, style);
            if (segments.Count == 0) return;

            var penX = xAttr ?? 0;
            var baseline = yAttr ?? 0;
            var ownStyle = style;

            foreach (var seg in segments)
            {
                if (string.IsNullOrWhiteSpace(seg.Text)) continue;
                if (seg.StyleOverride is not null) ownStyle = seg.StyleOverride;

                if (seg.X is not null) penX = seg.X.Value;
                if (seg.Y is not null) baseline = seg.Y.Value;
                penX += seg.Dx;
                baseline += seg.Dy;

                var sizeUser = ownStyle.FontSizeUser;
                var sizeMm = sizeUser * m.StrokeScale;
                var sizePt = Mm.MmToPoint(sizeMm);
                if (sizePt is < TemplateValidator.MinFontPt or > TemplateValidator.MaxFontPt)
                {
                    WarnOnce("fontfit", $"底稿里有 {sizePt:0.#}pt 的文字，超出 {TemplateValidator.MinFontPt}~{TemplateValidator.MaxFontPt}pt 的可印范围，已夹到范围内。");
                    sizePt = Math.Clamp(sizePt, TemplateValidator.MinFontPt, TemplateValidator.MaxFontPt);
                }

                var widthMm = EstimateWidthMm(seg.Text, sizeMm);
                var (mappedLeft, mappedBaseline) = m.Map(penX, baseline);
                var left = ownStyle.Anchor switch
                {
                    SvgTextAnchor.Middle => mappedLeft - widthMm / 2,
                    SvgTextAnchor.End => mappedLeft - widthMm,
                    _ => mappedLeft,
                };
                if (seg.X is null && ownStyle.Anchor != SvgTextAnchor.Start)
                    WarnOnce("anchor", "同一个 <text> 里有多段 <tspan> 且带对齐（text-anchor），后续段落的位置是按估算宽度推的，导入后请微调。");

                var ascent = sizeMm * 0.82;
                var top = mappedBaseline - ascent;
                var height = sizeMm * 1.25;
                var bounds = SvgBounds.Empty.Add(left, top).Add(left + widthMm, top + height);

                var fill = BuildPaint(ownStyle.Fill, ownStyle.FillOpacity, dash: null, strokeWidthUser: 1, scale: m.StrokeScale);
                _doc.Add(new SvgText
                {
                    Content = seg.Text,
                    XMm = left,
                    BaselineYmm = mappedBaseline,
                    SizePt = sizePt,
                    FontFamily = ownStyle.FontFamily,
                    Bold = ownStyle.Bold,
                    Anchor = ownStyle.Anchor,
                    Fill = fill,
                    Id = seg.Id,
                    WidthEstimateMm = widthMm,
                    Bounds = bounds,
                    SourceTag = seg.IsTspan ? "tspan" : "text",
                });

                // 笔沿水平方向推进下一段（估算宽→用户单位），除非下一段自己显式给了 x
                penX += m.StrokeScale > 1e-9 ? widthMm / m.StrokeScale : widthMm;
            }
        }

        private sealed record TextSegment(string Text, string? Id, bool IsTspan,
            double? X, double? Y, double Dx, double Dy, SvgStyle? StyleOverride);

        private List<TextSegment> CollectTextSegments(XElement text, SvgStyleSheet sheet, SvgStyle incoming)
        {
            var list = new List<TextSegment>();
            var parentStyle = incoming; // Walk 里已经把 <text> 自己的表现属性与 CSS 叠上了

            if (((string?)text.Attribute("textLength")) is { Length: > 0 })
                WarnOnce("textLength", "底稿文字带了 textLength（拉伸到指定长度），Core 里没有字体度量，宽度按估算给，导入后请核对。");
            if (((string?)text.Attribute("x")) is { } rawX && SvgMatrix.ParseNumberList(rawX).Count > 1)
                WarnOnce("textx", "底稿里一个 <text> 的 x 写了多个值（逐字定位），只取第一个，字间距会回到默认。");

            // 按文档序走:裸文本与 <tspan> 谁在前就谁先印。旧写法把裸文本整段插到所有 tspan 之前,
            // "Total: <tspan>5</tspan> pcs" 会印成 "5Total: pcs"(第 23 棒)。
            // 只有第一段裸文本继承 <text> 自己的 x/y 与 dx/dy,后面的段靠笔推进衔接。
            // 段与段衔接处的空格要留住( Collapse 会掐边),词距才不会丢。
            var bare = new System.Text.StringBuilder();
            var emittedBare = false;
            void FlushBare(bool final)
            {
                var raw = bare.ToString();
                bare.Clear();
                if (string.IsNullOrWhiteSpace(raw)) return;
                var content = Collapse(raw);
                if (content.Length == 0) return;
                if (list.Count > 0 && char.IsWhiteSpace(raw[0])) content = " " + content;
                if (!final && char.IsWhiteSpace(raw[^1])) content += " ";
                list.Add(new TextSegment(content, (string?)text.Attribute("id"), IsTspan: false,
                    emittedBare ? null : NumOpt(text, "x"),
                    emittedBare ? null : NumOpt(text, "y"),
                    emittedBare ? 0 : SumNum(text, "dx"),
                    emittedBare ? 0 : SumNum(text, "dy"),
                    parentStyle));
                emittedBare = true;
            }

            foreach (var node in text.Nodes())
            {
                if (node is XText rawText)
                {
                    bare.Append(rawText.Value);
                    continue;
                }
                if (node is not XElement element) continue;

                var local = element.Name.LocalName.ToLowerInvariant();
                if (local == "tspan")
                {
                    FlushBare(final: false);
                    var style = parentStyle.Overlay(element, sheet, _ux);
                    list.Add(new TextSegment(
                        Collapse(element.Value),
                        (string?)element.Attribute("id"),
                        IsTspan: true,
                        X: NumOpt(element, "x"),
                        Y: NumOpt(element, "y"),
                        Dx: SumNum(element, "dx"),
                        Dy: SumNum(element, "dy"),
                        StyleOverride: style));
                }
                else if (local is "a" or "textpath")
                {
                    FlushBare(final: false);
                    WarnOnce(local, $"<text> 里出现 <{local}>，只按普通文字取出内容。");
                    foreach (var inner in element.Elements().Where(n => n.Name.LocalName.Equals("tspan", StringComparison.OrdinalIgnoreCase)))
                    {
                        FlushBare(final: false);
                        list.Add(new TextSegment(Collapse(inner.Value), (string?)inner.Attribute("id"), true,
                            NumOpt(inner, "x"), NumOpt(inner, "y"), SumNum(inner, "dx"), SumNum(inner, "dy"),
                            parentStyle.Overlay(inner, sheet, _ux)));
                    }
                    // 只取本元素自己的直接文本,不再用 .Value(那会把上面已收的 tspan 再并一遍)
                    var self = Collapse(string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)));
                    if (self.Length > 0) list.Add(new TextSegment(self, null, true, null, null, 0, 0, parentStyle));
                }
                else if (local == "tref" || local == "altglyph")
                {
                    FlushBare(final: false);
                    WarnOnce(local, $"<text> 里有 <{local}>，这类文字取不出来，已留在底图里。");
                }
            }
            FlushBare(final: true);

            if (list.Count == 0)
            {
                var id = (string?)text.Attribute("id");
                list.Add(new TextSegment(string.Empty, id, false, NumOpt(text, "x"), NumOpt(text, "y"), 0, 0, parentStyle));
            }
            return list;
        }

        private static string Collapse(string? raw)
        {
            var s = (raw ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            var sb = new StringBuilder(s.Length);
            var lastSpace = false;
            foreach (var c in s)
            {
                if (c == ' ') { if (!lastSpace) sb.Append(' '); lastSpace = true; }
                else { sb.Append(c); lastSpace = false; }
            }
            return sb.ToString().Trim();
        }

        // ---- 图片 ----

        private void EmitImage(XElement element, SvgMatrix m, SvgStyle style)
        {
            var href = (string?)element.Attribute("href") ?? (string?)element.Attribute(_hrefAttr);
            var x = Num(element, "x");
            var y = Num(element, "y");
            var w = Num(element, "width");
            var h = Num(element, "height");
            if (string.IsNullOrWhiteSpace(href))
            {
                WarnOnce("image", "有一处 <image> 没有 href，已跳过。");
                return;
            }
            if (w <= 0 || h <= 0)
            {
                WarnOnce("image", "有一处 <image> 没写宽或高，位图没有内在尺寸可推，已跳过。");
                return;
            }

            byte[] bytes;
            string mime;
            var comma = href.IndexOf(',');
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            {
                var header = href[5..comma];
                mime = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ? header[..^7] : header;
                try
                {
                    bytes = Convert.FromBase64String(href[(comma + 1)..].Trim());
                }
                catch (FormatException)
                {
                    WarnOnce("image", "有一处内嵌位图的 base64 解码失败，已跳过该图。");
                    return;
                }
            }
            else
            {
                WarnOnce("image", $"底稿引用了外链图片「{Truncate(href, 40)}」，导入不会去取外部文件，该处只留空缺。");
                return;
            }

            var p1 = m.Map(x, y);
            var p2 = m.Map(x + w, y + h);
            var bounds = SvgBounds.Empty.Add(p1.X, p1.Y).Add(p2.X, p2.Y);
            _doc.Add(new SvgImage
            {
                Bytes = bytes,
                MimeType = string.IsNullOrWhiteSpace(mime) ? "image/png" : mime,
                Bounds = bounds,
                Id = (string?)element.Attribute("id"),
            });
        }

        // ---- use ----

        private void EmitUse(XElement element, SvgMatrix m, SvgStyleSheet sheet, SvgStyle style, int depth, int insideUse)
        {
            if (insideUse > 6)
            {
                WarnOnce("use", "底稿里 <use> 引用嵌套过深（可能有循环引用），已停止展开。");
                return;
            }
            var href = (string?)element.Attribute("href") ?? (string?)element.Attribute(_hrefAttr);
            if (string.IsNullOrWhiteSpace(href) || href[0] != '#')
            {
                WarnOnce("use", "有一处 <use> 引用的是外部文件或没给 id，无法展开，该内容不会出现在底图里。");
                return;
            }
            if (!_byId.TryGetValue(href[1..], out var target))
            {
                WarnOnce("use-missing", $"<use> 指向的 id「{href[1..]}」在文件里找不到，该处为空。");
                return;
            }

            var inner = SvgMatrix.Translate(Num(element, "x"), Num(element, "y")).Then(m);
            var local = target.Name.LocalName.ToLowerInvariant();
            if (local is "symbol" or "svg")
            {
                foreach (var child in target.Elements()) Walk(child, inner, sheet, style, depth + 1, insideUse + 1);
            }
            else
            {
                // 引用的是普通元素：把它当在当前位置重画一遍（id 去掉，避免同一 id 出现两次）
                Walk(CloneWithoutId(target), inner, sheet, style, depth + 1, insideUse + 1);
            }
        }

        private static XElement CloneWithoutId(XElement source)
        {
            var clone = new XElement(source);
            clone.Attribute("id")?.Remove();
            return clone;
        }

        // ---- 取值工具 ----

        private double Num(XElement e, string name) => NumOpt(e, name) ?? 0;

        private double? NumOpt(XElement e, string name)
        {
            var raw = (string?)e.Attribute(name);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var trimmed = raw.Trim();
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
            // 带单位的数值("10mm"):旧写法 ParseNumberList 只取数字、单位被静默扔掉——
            // viewBox 不是 1:1 时形状会错,至少要说一声(第 23 棒)。
            if (HasUnitSuffix(trimmed))
                WarnOnce("svg-num-unit", "SVG 数值属性带了单位（如 10mm）：已按数字处理，单位本身被忽略。画布是 1 用户单位 = 1 毫米，若源文件画布不同，尺寸请核对。");
            var nums = SvgMatrix.ParseNumberList(trimmed);
            return nums.Count > 0 ? nums[0] : null;
        }

        private double SumNum(XElement e, string name)
        {
            var raw = (string?)e.Attribute(name);
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            var trimmed = raw.Trim();
            if (HasUnitSuffix(trimmed))
                WarnOnce("svg-num-unit", "SVG 数值属性带了单位（如 10mm）：已按数字处理，单位本身被忽略。画布是 1 用户单位 = 1 毫米，若源文件画布不同，尺寸请核对。");
            var sum = 0d;
            foreach (var v in SvgMatrix.ParseNumberList(raw)) sum += v;
            return sum;
        }

        /// <summary>数字开头、字母结尾 = 带了单位后缀（"10mm"、"5pt"；纯数字与百分数不算）。</summary>
        private static bool HasUnitSuffix(string trimmed)
            => trimmed.Length > 1 && char.IsLetter(trimmed[^1]) && (char.IsDigit(trimmed[0]) || trimmed[0] is '.' or '-' or '+');

        private static SvgPathCommand Cmd(char c, params double[] args) => new(c, args);

        private static SvgPathCommand Cubic(double c1x, double c1y, double c2x, double c2y, double x, double y)
            => new('C', new[] { c1x, c1y, c2x, c2y, x, y });

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        private void WarnOnce(string key, string message)
        {
            if (!_warnedOnce.Add(key)) return;
            _issues.Add(new TemplateIssue(IssueLevel.Warning, message, ElementIndex: -1));
        }
    }
}
