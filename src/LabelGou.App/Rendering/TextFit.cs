using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LabelGou.Core.Layout;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>
/// 一段文字最终"怎么落"的决定结果：用几号字、往下挪多少、排好的 <see cref="FormattedText"/> 是哪一份。
/// <para>坐标都在设备单位（DIU）域，<see cref="BoxDiu"/> 是元素框，<see cref="TextTopDiu"/> 已经含垂直居中偏移。</para>
/// </summary>
public sealed class TextFitResult
{
    /// <summary>按调用方 scale 缩放后的元素框。</summary>
    public required Rect BoxDiu { get; init; }

    /// <summary>最终字号（DIU），已含缩字号结果与 scale。</summary>
    public required double EmSizeDiu { get; init; }

    /// <summary>未缩放（scale=1）时的字号（DIU）。矢量出口用它反算毫米，与预览同源。</summary>
    public required double CanonicalEmSizeDiu { get; init; }

    /// <summary>文字顶边的绝对 Y（DIU），可直接交给 <c>DrawText</c>。</summary>
    public required double TextTopDiu { get; init; }

    /// <summary>
    /// 墨迹左缘的绝对 X（DIU），与 <see cref="TextTopDiu"/> 一起交给 <c>DrawText</c>。
    /// <para>折行的文本 = 盒左边（对齐由 <c>FormattedText.TextAlignment</c> 在盒宽内做掉，历史行为一字不变）；
    /// <strong>永不折行</strong>（第 46 棒）时 <c>MaxTextWidth</c> 放到无限宽，WPF 的对齐就没了参照，
    /// 所以偏移在这里自己算——两条出口（<c>LabelRenderer</c> 与 <c>SheetSvgWriter</c>）都读这一个值，不许各推一遍。</para>
    /// </summary>
    public required double InkLeftDiu { get; init; }

    /// <summary>
    /// 这一排字<strong>看得见的那一块</strong>（绝对 DIU，已含调用方的 scale）：左、上、宽、高。
    /// <para>为什么不能拿 <see cref="FormattedText.Height"/> 那个行盒当它：行盒含字体内部 leading，
    /// 第 81 棒拿用户那行 59pt 的字实测——行盒 23.98mm、真字形 15.25mm，编辑器把行盒画成选中框，
    /// 于是"框比字高一倍"，再乘上他自己填的纵向拉伸 2.133 就是截图里那 27mm 富余。</para>
    /// <para>为什么对齐偏移也并到这里一次算：折行那条路的居中是 WPF 在盒宽内做的，
    /// <see cref="InkLeftDiu"/> 因此只等于盒左边；量具照抄它就漏掉了那一格偏移，实测框比字往左挪
    /// <strong>17.9mm</strong>（用户圈的就是这个）。这里从字形外接算，两条路共用同一份结果，
    /// 不再手写第二套居中算术。</para>
    /// </summary>
    public required (double X, double Y, double Width, double Height) InkDiu { get; init; }

    /// <summary>墨迹中心＝渲染端的旋转锚点（五出口共用；第 52 棒说的"绕看得见那块转"到本棒才真的量得出来）。</summary>
    public (double Cx, double Cy) InkCenterDiu => (InkDiu.X + InkDiu.Width / 2, InkDiu.Y + InkDiu.Height / 2);

    /// <summary>请求字号与实际字号之比（1 = 没缩；小于 1 = 触发过缩字号收敛）。</summary>
    public required double ShrinkRatio { get; init; }

    /// <summary>排好版的文本，已按 <see cref="BoxDiu"/> 宽度做换行与省略号截断。</summary>
    public required FormattedText Formatted { get; init; }

    /// <summary>排出来实际几行（规范域的决定，与 <c>scale</c> 无关）。多行文字在 SVG 出口里不能写成 <c>&lt;text&gt;</c>，靠它判定。</summary>
    public required int LineCount { get; init; }

    /// <summary>是否因放不下而缩过字号。</summary>
    public bool Shrunk => ShrinkRatio < 0.999;

    /// <summary>
    /// 缩到下限后仍然装不下，被省略号吃掉了内容。
    /// <para>唛头上货号被截断等于打错货，所以这个标志必须存在并且两条出口都得把它画成警示色（不许静默）。</para>
    /// </summary>
    public required bool Truncated { get; init; }
}

/// <summary>
/// 文字排版的唯一决定者（M5 定案 D10）：缩字号收敛 + 垂直居中 + 换行截断，只此一份实现。
/// <para>
/// 为什么必须抽出来：预览、位图导出、打印之外，本棒又加了 SVG 矢量出口。
/// 如果"这行字最后用几号字、被截成什么样"在出口里再算一遍，两条出口必然漂移（§七-11 铁律）。
/// 现在 <see cref="LabelRenderer"/> 与 <c>SheetSvgWriter</c> 都调 <see cref="Solve"/>，
/// 前者直接 <c>DrawText</c>，后者拿 <see cref="FormattedText.BuildGeometry"/> 转曲。
/// </para>
/// <para>
/// <strong>关键设计：适配决定恒在 scale=1 的规范域里做</strong>，再按比例乘回调用方的 scale。
/// 原来的实现把 <c>formatted.Height &lt;= box.Height + 0.5</c> 这个带绝对容差的判断放在缩放后的域里，
/// 于是屏幕放大倍数会改变"放得下几行"的判定——同一个模板，预览缩了字而导出没缩。
/// 抽出来顺手把这个不一致消掉：屏幕上看见的字号比例，就是印出来的比例。
/// </para>
/// </summary>
public static class TextFit
{
    /// <summary>缩字号收敛最多试这么多轮（每轮 <see cref="ShrinkStep"/>）。</summary>
    public const int MaxShrinkSteps = 24;

    /// <summary>每轮缩字号的乘数。</summary>
    public const double ShrinkStep = 0.92;

    /// <summary>多行文字最多缩到请求字号的 62%，再小就宁可截断也不印蚂蚁字。</summary>
    public const double MinEmSizeRatio = 0.62;

    /// <summary>
    /// 单行文字（<c>MaxLines == 1</c>）可以缩到 30%。
    /// <para>为什么单行要另开一档：62% 这个比率是给"人挑的 12pt 明细字"防蚂蚁字用的；
    /// 而行式骨架的撑满大字，字号是从行带高度反算出来的几何量，不是谁挑的字号——
    /// 110pt 的 62% 是 68pt，可厂商那张 160×120 的第二行本来就装不下 68pt，
    /// 结果就是把 <c>AJ7-QI YUE: Aj9</c> 印成 <c>AJ7-…</c>（真样件回归里肉眼抓到的）。
    /// 单行还另有 <see cref="MinReadablePt"/> 绝对下限兜底，不会真缩成看不见。</para>
    /// </summary>
    public const double MinSingleLineEmSizeRatio = 0.30;

    /// <summary>绝对可读下限（磅）：不管比率怎么算，印刷文字不许低于这个字号。</summary>
    public const double MinReadablePt = 9;

    /// <summary>判定"放得下"时允许的余量（DIU，规范域），抵掉行高的浮点噪声。</summary>
    public const double HeightSlackDiu = 0.5;

    /// <summary>宽度方向同样的余量。抵掉 FormattedText 末尾字距的浮点噪声。</summary>
    public const double WidthSlackDiu = 0.5;

    /// <summary>量宽时用这个"足够宽"的框，让 FormattedText 既不折行也不加省略号。</summary>
    private const double UnboundedWidthDiu = 1_000_000;

    /// <summary>行高比率兜底（雅黑实测 1.32）：单行样本量不出来时用它，不能让行数算成 0 而除零。</summary>
    public const double DefaultLineHeightRatio = 1.35;

    /// <summary>规范域的取样密度：1 像素 = 1 DIU。决定拟合结果，不影响最终绘制清晰度。</summary>
    public const double CanonicalPixelsPerDip = 1.0;

    /// <summary>请求字号（磅）→ 规范域字号（DIU）。1pt = 96/72 DIU。</summary>
    public static double RequestedEmSizeDiu(double fontSizePt) => Mm.ToDiu(Mm.PointToMm(fontSizePt));

    /// <summary>元素框（毫米）→ 指定 scale 下的 DIU 矩形。</summary>
    public static Rect BoxInDiu(TextItem text, double scale) => new(
        Mm.ToDiu(text.X) * scale, Mm.ToDiu(text.Y) * scale,
        Math.Max(0, Mm.ToDiu(text.Width) * scale), Math.Max(0, Mm.ToDiu(text.Height) * scale));

    /// <summary>字体族回退 + 粗体，两条出口共用（同一段文字不能预览用宋体、导出用回退字体）。</summary>
    public static Typeface TypefaceFor(TextItem text) => new(
        RenderRules.SafeFontFamily(text.FontFamily),
        FontStyles.Normal,
        text.Bold ? FontWeights.Bold : FontWeights.Normal,
        FontStretches.Normal);

    /// <summary>
    /// 解出一段文字的落位。<paramref name="scale"/> 只影响输出坐标，不影响"缩了几档"的决定。
    /// </summary>
    /// <param name="text">版面里的文字项。</param>
    /// <param name="scale">调用方的显示/渲染缩放倍数（屏幕预览为 zoom，导出与打印为 1）。</param>
    /// <param name="pixelsPerDip">当前 DPI 系数，只用于最终那份 <see cref="FormattedText"/> 的光栅化参数。</param>
    public static TextFitResult? Solve(TextItem text, double scale, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) return null;

        var box = BoxInDiu(text, scale);
        var canonicalBox = BoxInDiu(text, 1.0);
        if (canonicalBox.Width <= 0 || canonicalBox.Height <= 0) return null;   // 零面积元素画不出东西，交给校验器报越界

        var typeface = TypefaceFor(text);
        var foreground = ForegroundFor(text);
        var requested = RequestedEmSizeDiu(text.FontSizePt);

        // 单行元素不许折行，所以宽度不够只能缩字号；多行元素宽度不够是折行，不该动字号。
        var mustStayOnOneLine = text.MaxLines == 1;
        var ratioFloor = mustStayOnOneLine ? MinSingleLineEmSizeRatio : MinEmSizeRatio;
        var minEmSize = Math.Max(requested * ratioFloor, RequestedEmSizeDiu(MinReadablePt));

        // 行高：本机实测 FormattedText.LineHeight 返回 0（取证：artifacts-review/textfit-probe.txt），
        // 所以行数不能靠它，只能拿同字号的单行样本反推一个比率，再按 em 线性缩放。
        var lineHeightRatio = SingleLineHeightRatio(typeface, requested);

        // 第 46 棒：**折行边界与对齐基准分家**。永不折行时用"无限宽"做折行与宽度判据（宽度不再参与缩字），
        // 盒宽只留给对齐与垂直居中当参照；折行的文本一切照旧。
        var wrapCanonical = text.NoWrap ? UnboundedWidthDiu : canonicalBox.Width;
        var wrapFinal = text.NoWrap ? UnboundedWidthDiu : box.Width;

        // —— 规范域（scale=1）里收敛字号，得到与缩放无关的唯一决定 ——
        var emSize = requested;
        var need = Measure(text, typeface, foreground, emSize, wrapCanonical, lineHeightRatio);

        // 缩字号的目标：装得下（高度按会显示的那几行算，单行还得多一道宽度）。
        bool DoesNotFit(TextNeed n) => n.ShownHeight > canonicalBox.Height + HeightSlackDiu
            || (mustStayOnOneLine && n.FlatWidth > wrapCanonical + WidthSlackDiu);

        // 内容真的被吃掉了：行被上限砍掉，或单行宽度不够被省略号替换。
        // 这里不能把"文字比框高"也算进来：那是几何越界（校验器报），不是截断，
        // 混在一起会让一堆行高刚好贴边的历史模板整片变红。
        bool ContentLost(TextNeed n) => n.NaturalLines > n.ShownLines
            || (mustStayOnOneLine && n.FlatWidth > wrapCanonical + WidthSlackDiu);

        if (text.ShrinkToFit)
        {
            var guard = 0;
            // 下一档就跌破下限 → 停在当前档：宁可截断，也不印低于下限的蚂蚁字
            while (guard++ < MaxShrinkSteps && DoesNotFit(need) && emSize * ShrinkStep >= minEmSize)
            {
                emSize *= ShrinkStep;
                need = Measure(text, typeface, foreground, emSize, wrapCanonical, lineHeightRatio);
            }
        }

        var truncated = ContentLost(need);

        // —— 按调用方的 scale 出最终那一份（换行/省略号由 MaxTextWidth 驱动，这里必须用缩放后的折行宽度）——
        // 截断与未解析字段同一个语义：这块印出来的东西必须有人看，所以用警示色画。
        var finalInk = truncated ? RenderRules.FlagInk : foreground;
        var centeredOffset = Math.Max(0, (canonicalBox.Height - need.ShownHeight) / 2);

        var finalEmSize = emSize * scale;
        var formatted = Build(text, typeface, finalInk, finalEmSize, wrapFinal, pixelsPerDip);
        var offsetY = box.Top + centeredOffset * scale;
        var inkLeft = box.Left;
        var rawInkWidth = Math.Max(0, formatted.WidthIncludingTrailingWhitespace);
        if (text.NoWrap)
        {
            // 对齐偏移自己算（Build 在这条路上强制左对齐）。墨迹比盒宽时偏移为负——
            // 居中的长字会左右各伸出去一截，这正是"超出去看得见"要的，不夹。
            inkLeft = text.Align switch
            {
                HorizontalAlign.Center => box.Left + (box.Width - rawInkWidth) / 2,
                HorizontalAlign.Right => box.Right - rawInkWidth,
                _ => box.Left,
            };
        }

        var ink = CanonicalRelativeInk(text, typeface, finalInk, emSize, canonicalBox.Width, canonicalBox.Height,
                wrapCanonical, need.ShownHeight)
            is { } rel
            ? (box.Left + rel.X * scale, box.Top + rel.Y * scale, rel.Width * scale, rel.Height * scale)
            // 量不到字形（纯空白那类）：退回行盒那一条，与从前逐字相同——不猜
            : (inkLeft, offsetY,
                Math.Max(0.1, text.NoWrap ? rawInkWidth : Math.Min(box.Width, rawInkWidth)),
                Math.Max(0.1, formatted.Height));

        return new TextFitResult
        {
            BoxDiu = box,
            EmSizeDiu = finalEmSize,
            CanonicalEmSizeDiu = emSize,
            TextTopDiu = offsetY,
            InkLeftDiu = inkLeft,
            InkDiu = ink,
            ShrinkRatio = requested > 0 ? emSize / requested : 1,
            Formatted = formatted,
            LineCount = need.ShownLines,
            Truncated = truncated,
        };
    }

    /// <summary>量尺寸的结果：真实会占掉的高度、显示几行、自然有几行、以及单行时"绝不折行有多宽"。</summary>
    private readonly record struct TextNeed(double ShownHeight, int ShownLines, int NaturalLines, double FlatWidth);

    /// <summary>需人工核对的字段一律标红（警示压过一切），其余用元素自己那支墨（没填 = 黑）。</summary>
    public static Brush ForegroundFor(TextItem text) => text.Flagged ? RenderRules.FlagInk : RenderRules.InkOf(text.Ink);

    /// <summary>
    /// 排出来实际几行。WPF 的 <see cref="FormattedText"/> 没有 <c>LineCount</c> 属性，
    /// 而本机 <c>LineHeight</c> 实测返回 0，所以只能拿"同字号单行样本高"去除总高。
    /// </summary>
    public static int LineCountOf(double totalHeight, double lineHeight)
        => lineHeight > 0
            ? Math.Max(1, (int)Math.Round(totalHeight / lineHeight, MidpointRounding.AwayFromZero))
            : 1;

    /// <summary>同字体同字号下"一行"占多高 / em 的比率。</summary>
    private static double SingleLineHeightRatio(Typeface typeface, double emSize)
    {
        if (emSize <= 0) return DefaultLineHeightRatio;
        var sample = new FormattedText(
            "H", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, emSize, Brushes.Black, CanonicalPixelsPerDip)
        {
            MaxTextWidth = UnboundedWidthDiu,
        };
        return sample.Height > 0 ? sample.Height / emSize : DefaultLineHeightRatio;
    }

    /// <summary>
    /// 建一份 <see cref="FormattedText"/>。
    /// <para>WPF 的 FormattedText 没有 TextWrapping 成员（属性/构造参都没有），
    /// 换行完全由 MaxTextWidth 驱动；行数上限用 MaxLineCount + Trimming。</para>
    /// </summary>
    public static FormattedText Build(
        TextItem text, Typeface typeface, Brush foreground, double emSize, double maxWidth, double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text.Content,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            emSize,
            foreground,
            pixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            // 永不折行时 maxWidth 是无限宽，对齐若交给 WPF 会把字甩到无限宽的正中间；
            // 这条路的偏移由 TextFitResult.InkLeftDiu 自己算（第 46 棒）。折行的路一切照旧。
            TextAlignment = text.NoWrap ? TextAlignment.Left : text.Align switch
            {
                HorizontalAlign.Center => TextAlignment.Center,
                HorizontalAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
            Trimming = TextTrimming.CharacterEllipsis,
        };

        if (text.MaxLines > 0) formatted.MaxLineCount = text.MaxLines;
        return formatted;
    }

    /// <summary>
    /// 字形墨迹<strong>相对排版盒左上角</strong>的位置与尺寸（规范域 DIU）。
    /// <para>必须记忆：实测 <c>BuildGeometry</c> 单价 0.5~1.2ms，而编辑器每帧要给最多 80 个元素量墨迹
    /// （拖动中每帧重建版面），不记忆就是 112ms/帧——用户看到的"拖一下卡一下"就是这么来的。
    /// 这份相对量与元素的 X/Y 无关（摆位只把整块平移），所以拖动一帧都不重算。</para>
    /// <para><strong>键里不许漏字段</strong>：漏一个就会把上一版的墨迹当这一版的用，
    /// 那是 §五-62 那一族（两套算术各自长歪）里最难查的一种——错得安静。
    /// 凡 <see cref="Build"/> 与缩字号收敛吃得到的输入都在这里：内容、字族、字号、粗体、对齐、
    /// 是否缩字、行数上限、排版盒宽高、折行宽度。笔色与旋转/拉伸不在其中（前者只改颜色，后者由调用方叠）。</para>
    /// </summary>
    private static (double X, double Y, double Width, double Height)? CanonicalRelativeInk(
        TextItem text, Typeface typeface, Brush foreground, double emSize,
        double boxWidth, double boxHeight, double wrapCanonical, double shownHeight)
    {
        // 键里存的是 SafeFontFamily 的**输入**（原始字族名）：映射是确定的，存输入比存投影出来的对象稳。
        var key = new InkKey(text.Content, text.FontFamily ?? string.Empty, text.FontSizePt, text.Bold,
            text.Align, text.ShrinkToFit, text.MaxLines, boxWidth, boxHeight, text.WrapWidthMm);
        if (RelativeInkCache.TryGetValue(key, out var cached)) return cached;

        var computed = ComputeRelativeInk(text, typeface, foreground, emSize, boxWidth, boxHeight, wrapCanonical, shownHeight);
        if (RelativeInkCache.Count > RelativeInkCacheCap) RelativeInkCache.Clear();   // 纯函数：挤掉只是再付一次钱
        RelativeInkCache[key] = computed;
        return computed;
    }

    private static (double X, double Y, double Width, double Height)? ComputeRelativeInk(
        TextItem text, Typeface typeface, Brush foreground, double emSize,
        double boxWidth, double boxHeight, double wrapCanonical, double shownHeight)
    {
        var canonical = Build(text, typeface, foreground, emSize, wrapCanonical, CanonicalPixelsPerDip);
        var centered = Math.Max(0, (boxHeight - shownHeight) / 2);
        var flatWidth = Math.Max(0.1, canonical.WidthIncludingTrailingWhitespace);
        // 永不折行那条路的对齐偏移（与 Solve 里给 DrawText 的那一份同一个算式，不另推一遍）
        var alignOffset = text.NoWrap ? text.Align switch
        {
            HorizontalAlign.Center => (boxWidth - flatWidth) / 2,
            HorizontalAlign.Right => boxWidth - flatWidth,
            _ => 0d,
        } : 0d;   // 折行那条路：居中由 WPF 在 MaxTextWidth 内做掉，字形外接里已经含着

        if (InkBoundsOf(canonical) is not { } glyph)
            return (alignOffset, centered,
                text.NoWrap ? flatWidth : Math.Min(Math.Max(0.1, boxWidth), flatWidth),
                Math.Max(0.1, canonical.Height));

        return (alignOffset + glyph.X, centered + glyph.Y,
            Math.Max(0.1, glyph.Width), Math.Max(0.1, glyph.Height));
    }

    /// <summary>
    /// 一份 <see cref="FormattedText"/> 真正会印出去的那一块（相对它自己的绘制原点）。
    /// <para>空白内容、零面积、NaN/无穷一律算"量不到"（返回 null）——量不到就交回行盒那一条，
    /// 绝不拿 <c>Rect.Empty</c> 的无穷边界去撑出一个假框。</para>
    /// </summary>
    private static Rect? InkBoundsOf(FormattedText formatted)
    {
        try
        {
            var bounds = formatted.BuildGeometry(new Point(0, 0)).Bounds;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
            if (double.IsNaN(bounds.Width) || double.IsInfinity(bounds.Width)
                || double.IsNaN(bounds.Height) || double.IsInfinity(bounds.Height)) return null;
            return bounds;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private sealed record InkKey(string Content, string FontFamily, double FontSizePt, bool Bold,
        HorizontalAlign Align, bool ShrinkToFit, int MaxLines, double BoxWidth, double BoxHeight, double WrapWidthMm);

    /// <summary>
    /// 墨迹记忆表。<c>ConcurrentDictionary</c> 不是讲究：单测按类并行、编辑器画布在 STA 线程上量，
    /// 撞一次就是脏读。重复计算无害（纯函数），所以只在超上限时整表清掉，不做逐条淘汰。
    /// </summary>
    private static readonly ConcurrentDictionary<InkKey, (double X, double Y, double Width, double Height)?> RelativeInkCache = new();

    /// <summary>记忆表上限：一份模板最多 80 个元素，4096 格足够装下几千个不同值；超了就整表重来。</summary>
    private const int RelativeInkCacheCap = 4096;

    /// <summary>
    /// 量尺寸：<c>ShownHeight</c> 取"按真实框宽折行、并按元素自己的行数上限"那一份的高度（也就是最终真会占掉多高）。
    /// <para>不能拿无限宽的那份量高：单行元素不折行，高度永远刚好等于行带，看不出宽度已经装不下。
    /// 也不能拿不限行数的那份量高度目标：10 行内容塞 3 行的框，会为了把 10 行都塞进去而一路缩到下限。</para>
    /// <para>只有单行元素才额外量一份无限宽的 <c>FlatWidth</c>：多行元素宽度不够是折行，不该动字号。</para>
    /// </summary>
    private static TextNeed Measure(
        TextItem text, Typeface typeface, Brush foreground, double emSize, double boxWidth, double lineHeightRatio)
    {
        var lineHeight = emSize * lineHeightRatio;
        var shown = Build(text, typeface, foreground, emSize, boxWidth, CanonicalPixelsPerDip);
        var shownLines = LineCountOf(shown.Height, lineHeight);
        // 行数上限会不会真的掉行，得拿"不限行数"那一份比：不建则不知道内容自然排成几行。
        var naturalLines = shownLines;
        if (text.MaxLines > 0 && shownLines == text.MaxLines)
        {
            var uncapped = Build(text, typeface, foreground, emSize, boxWidth, CanonicalPixelsPerDip);
            uncapped.MaxLineCount = int.MaxValue;
            naturalLines = LineCountOf(uncapped.Height, lineHeight);
        }

        if (text.MaxLines != 1)
            return new TextNeed(shown.Height, shownLines, naturalLines, 0);

        var flat = Build(text, typeface, foreground, emSize, UnboundedWidthDiu, CanonicalPixelsPerDip);
        flat.MaxLineCount = int.MaxValue;
        return new TextNeed(shown.Height, shownLines, naturalLines, flat.Width);
    }
}
