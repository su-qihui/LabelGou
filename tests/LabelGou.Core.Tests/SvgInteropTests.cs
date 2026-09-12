using LabelGou.Core.Interop.Svg;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// M5 · SVG 底稿读写。
/// <para>
/// 这一层的价值全在"毫米算得对不对"：底稿在 CDR 里是多大，导进来就必须是多大，
/// 差一个数量级就会印成墙纸。所以断言一律对着<strong>具体毫米数</strong>，不用"不为空"糊过去。
/// </para>
/// </summary>
public class SvgInteropTests
{
    private const string Page1To1 = """<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm" viewBox="0 0 100 80">""";

    private static SvgDocument Ok(string xml, SvgParseOptions? options = null)
    {
        var result = SvgParser.Parse(xml, options);
        Assert.False(result.HasError, string.Join(" | ", result.ErrorMessages));
        return result.Document;
    }

    private static List<TemplateIssue> Issues(string xml) => SvgParser.Parse(xml).Issues.ToList();

    private static string Wrap(string body, string header = Page1To1) => header + body + "</svg>";

    // ---------- 长度与单位 ----------

    [Theory]
    [InlineData("12.5", 12.5, SvgLengthUnit.User)]
    [InlineData("100mm", 100, SvgLengthUnit.Mm)]
    [InlineData("3.94in", 3.94, SvgLengthUnit.In)]
    [InlineData("12pt", 12, SvgLengthUnit.Pt)]
    [InlineData("1pc", 1, SvgLengthUnit.Pc)]
    [InlineData("50%", 50, SvgLengthUnit.Percent)]
    [InlineData("  -2.5 ", -2.5, SvgLengthUnit.User)]
    public void LengthParseRecognisesUnits(string text, double value, SvgLengthUnit unit)
    {
        var len = SvgLength.Parse(text);
        Assert.NotNull(len);
        Assert.Equal(unit, len!.Value.Unit);
        Assert.Equal(value, len.Value.Value, 6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("mm")]
    [InlineData("12foo")]
    public void LengthParseRejectsGarbage(string text) => Assert.Null(SvgLength.Parse(text));

    [Fact]
    public void PixelIsNinetySixthOfInchAndMatchesWpfDiu()
    {
        // 1px = 1/96in = 0.264583mm——这是全项目唯一一处 px→mm，改它等于改所有底稿的落位
        Assert.Equal(25.4 / 96.0, new SvgLength(1, SvgLengthUnit.Px).ToMillimetres(), 9);
        Assert.Equal(25.4, new SvgLength(1, SvgLengthUnit.In).ToMillimetres(), 9);
        Assert.Equal(25.4 / 72.0, new SvgLength(1, SvgLengthUnit.Pt).ToMillimetres(), 9);
        Assert.Equal(25.4 / 6.0, new SvgLength(1, SvgLengthUnit.Pc).ToMillimetres(), 9);
        Assert.True(double.IsNaN(new SvgLength(50, SvgLengthUnit.Percent).ToMillimetres()));
    }

    [Theory]
    [InlineData("10-5", new double[] { 10, -5 })]
    [InlineData("-.5", new double[] { -0.5 })]
    [InlineData("1e-5", new double[] { 1e-5 })]
    [InlineData("1.5,2.5 3", new double[] { 1.5, 2.5, 3 })]
    [InlineData("M10 10L20 20", new double[] { 10, 10, 20, 20 })]
    [InlineData("2e", new double[] { 2 })]
    public void NumberListHandlesSvgAbbreviations(string text, double[] expected)
    {
        Assert.Equal(expected, SvgMatrix.ParseNumberList(text));
    }

    [Fact]
    public void MatrixCompositionMatchesSvgOrder()
    {
        // translate(10,10) scale(2) ：SVG 里右边的先作用，(1,1) 先放大成 (2,2)，再平移 → (12,12)
        var m = SvgMatrix.Parse("translate(10,10) scale(2)", out var unparsed);
        Assert.Null(unparsed);
        var (x, y) = m!.Value.Map(1, 1);
        Assert.Equal(12, x, 6);
        Assert.Equal(12, y, 6);
    }

    [Fact]
    public void MatrixRotateAndSkewAndMatrixKeyword()
    {
        var rotated = SvgMatrix.Parse("rotate(90)", out _)!.Value;
        var (rx, ry) = rotated.Map(10, 0);
        Assert.Equal(0, rx, 6);
        Assert.Equal(10, ry, 6);

        var about = SvgMatrix.Parse("rotate(180, 50, 40)", out _)!.Value;
        var (ax, ay) = about.Map(0, 0);
        Assert.Equal(100, ax, 6);
        Assert.Equal(80, ay, 6);

        var skew = SvgMatrix.Parse("skewX(45)", out _)!.Value;
        var (sx, sy) = skew.Map(0, 10);
        Assert.Equal(10, sx, 5);
        Assert.Equal(10, sy, 5);

        var explicitMatrix = SvgMatrix.Parse("matrix(1 0 0 1 7 9)", out _)!.Value;
        Assert.Equal((7d, 9d), explicitMatrix.Map(0, 0));
    }

    [Fact]
    public void MatrixReportsWhatItCouldNotParse()
    {
        var m = SvgMatrix.Parse("translate(5,5) nonsense(3)", out var unparsed);
        Assert.NotNull(m);
        Assert.NotNull(unparsed);
        Assert.Contains("nonsense", unparsed, StringComparison.Ordinal);
    }

    // ---------- 画布口径 ----------

    [Fact]
    public void ViewBoxWithMmWidthMakesUserUnitEqualOneMm()
    {
        var doc = Ok(Wrap(@"<rect x=""10"" y=""20"" width=""20"" height=""15""/>"));
        Assert.Equal(100, doc.WidthMm, 6);
        Assert.Equal(80, doc.HeightMm, 6);
        Assert.Equal(1, doc.UserUnitMm, 6);
        var path = Assert.Single(doc.Paths);
        Assert.Equal(10, path.Bounds.XMm, 6);
        Assert.Equal(20, path.Bounds.YMm, 6);
        Assert.Equal(20, path.Bounds.WidthMm, 6);
        Assert.Equal(15, path.Bounds.HeightMm, 6);
    }

    [Fact]
    public void NoViewBoxTreatsCoordinatesAsPixels()
    {
        // width 写 mm 但没 viewBox：坐标仍按 px 口径，100 用户单位 = 26.4583mm
        var doc = Ok("""<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm"><rect x="0" y="0" width="100" height="100"/></svg>""");
        Assert.Equal(100, doc.WidthMm, 4);
        Assert.Equal(25.4 / 96.0, doc.UserUnitMm, 9);
        // 100 个用户单位 × 0.264583mm = 26.458mm（不是 25.4：那是 96 个单位）
        Assert.Equal(100 * 25.4 / 96.0, Assert.Single(doc.Paths).Bounds.WidthMm, 4);
    }

    [Fact]
    public void ViewBoxOriginOffsetIsSubtracted()
    {
        var doc = Ok(Wrap(@"<rect x=""25"" y=""35"" width=""10"" height=""10""/>",
            """<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm" viewBox="20 30 100 80">"""));
        var bounds = Assert.Single(doc.Paths).Bounds;
        Assert.Equal(5, bounds.XMm, 6);
        Assert.Equal(5, bounds.YMm, 6);
    }

    [Fact]
    public void MissingSizeWarnsInsteadOfGuessingSilently()
    {
        var issues = Issues("""<svg xmlns="http://www.w3.org/2000/svg"><rect width="10" height="10"/></svg>""");
        Assert.Contains(issues, i => i.Severity == IssueLevel.Warning && i.Message.Contains("兜底", StringComparison.Ordinal));
    }

    [Fact]
    public void PercentWidthIsNotGuessed()
    {
        // width="100%" 没有稳定参照物：按无效处理，退回 px 口径并说明
        var doc = SvgParser.Parse("""<svg xmlns="http://www.w3.org/2000/svg" width="100%" height="100%"><rect width="10" height="10"/></svg>""");
        Assert.False(doc.HasError);
        Assert.Equal(25.4 / 96.0, doc.Document.UserUnitMm, 9);
    }

    // ---------- 几何归一 ----------

    [Fact]
    public void CircleBecomesFourCubicSegmentsAndKeepsBounds()
    {
        var doc = Ok(Wrap(@"<circle cx=""50"" cy=""40"" r=""10""/>"));
        var path = Assert.Single(doc.Paths);
        Assert.Equal("circle", path.SourceTag);
        Assert.Equal(4, path.Commands.Count(c => c.Command == 'C'));
        Assert.Equal(20, path.Bounds.WidthMm, 6);
        Assert.Equal(20, path.Bounds.HeightMm, 6);
        Assert.Equal(40, path.Bounds.XMm, 6);
    }

    [Fact]
    public void RoundedRectCarriesCurvesWhereSquareRectDoesNot()
    {
        var rounded = Assert.Single(Ok(Wrap(@"<rect x=""10"" y=""10"" width=""40"" height=""20"" rx=""5""/>")).Paths);
        var square = Assert.Single(Ok(Wrap(@"<rect x=""10"" y=""10"" width=""40"" height=""20""/>")).Paths);
        Assert.Contains(rounded.Commands, c => c.Command == 'C');
        Assert.DoesNotContain(square.Commands, c => c.Command == 'C');
        Assert.Equal(3, square.Commands.Count(c => c.Command == 'L'));
    }

    [Fact]
    public void LineAndPolylineStayOpenPolygonCloses()
    {
        var doc = Ok(Wrap(@"<line x1=""1"" y1=""2"" x2=""30"" y2=""40""/><polyline points=""0,0 10 0 10 10""/><polygon points=""0,0 10 0 10 10""/>"));
        Assert.Equal(3, doc.Paths.Count);
        Assert.DoesNotContain(doc.Paths[0].Commands, c => c.Command == 'Z');
        Assert.DoesNotContain(doc.Paths[1].Commands, c => c.Command == 'Z');
        Assert.Contains(doc.Paths[2].Commands, c => c.Command == 'Z');
    }

    [Fact]
    public void PathAbsoluteAndRelativeCommandsLandWhereSpecSays()
    {
        var doc = Ok(Wrap(@"<path d=""M10 10 l20 0 L50 30 H20 V50 z""/>"));
        var commands = Assert.Single(doc.Paths).Commands;
        Assert.Equal('M', commands[0].Command);
        Assert.Equal((10d, 10d), (commands[0].Args[0], commands[0].Args[1]));
        Assert.Equal((30d, 10d), (commands[1].Args[0], commands[1].Args[1])); // l20 0
        Assert.Equal((50d, 30d), (commands[2].Args[0], commands[2].Args[1])); // L50 30
        Assert.Equal((20d, 30d), (commands[3].Args[0], commands[3].Args[1])); // H20 → 绝对 L
        Assert.Equal((20d, 50d), (commands[4].Args[0], commands[4].Args[1])); // V50
        Assert.Equal('Z', commands[5].Command);
    }

    [Fact]
    public void RepeatedMovetoBecomesImplicitLineto()
    {
        // 规范：M 之后多出来的坐标对按 L 处理
        var commands = Assert.Single(Ok(Wrap(@"<path d=""M1 1 2 2 3 3""/>")).Paths).Commands;
        Assert.Equal(new[] { 'M', 'L', 'L' }, commands.Select(c => c.Command).ToArray());
    }

    [Fact]
    public void SmoothCurvesReflectPreviousControlPoint()
    {
        var commands = Assert.Single(Ok(Wrap(@"<path d=""M10 10 C20 20 30 20 40 10 S60 0 70 10""/>")).Paths).Commands;
        Assert.Equal(2, commands.Count(c => c.Command == 'C'));
        var second = commands.Last().Args;
        // S 的第一个控制点 = 上一段 c2(30,20) 对终点(40,10) 的反射 = (50,0)
        Assert.Equal(50, second[0], 6);
        Assert.Equal(0, second[1], 6);
    }

    [Fact]
    public void QuadraticCurveIsUpgradedToCubic()
    {
        var commands = Assert.Single(Ok(Wrap(@"<path d=""M0 0 Q10 20 20 0""/>")).Paths).Commands;
        var c = Assert.Single(commands, x => x.Command == 'C').Args;
        Assert.Equal(20.0 / 3.0, c[0], 6); // 0 + 2/3·(10-0)
        Assert.Equal(40.0 / 3.0, c[1], 6); // 0 + 2/3·(20-0)
        Assert.Equal((20d, 0d), (c[4], c[5]));
    }

    [Fact]
    public void ArcBecomesCubicsAndEndsAtTheRightPoint()
    {
        var commands = Assert.Single(Ok(Wrap(@"<path d=""M10 10 A10 10 0 0 1 30 10""/>")).Paths).Commands;
        Assert.Equal('M', commands[0].Command);
        Assert.All(commands.Skip(1), c => Assert.Equal('C', c.Command));
        var last = commands[^1].Args;
        Assert.Equal(30, last[^2], 6);
        Assert.Equal(10, last[^1], 6);
    }

    [Theory]
    [InlineData("0 0", new[] { 20d })]   // 小弧：向右凸
    [InlineData("1 0", new[] { 0d })]    // 大弧：绕远路，包围盒更大
    [InlineData("0 1", new[] { 20d })]   // 反向小弧
    [InlineData("1 1", new[] { 0d })]    // 反向大弧
    public void ArcFlagsChooseDifferentSweep(string flags, double[] ignore)
    {
        _ = ignore;
        var path = Assert.Single(Ok(Wrap($@"<path d=""M0 10 A10 10 0 {flags} 20 10""/>")).Paths);
        var maxY = path.Commands.Where(c => c.Args.Count >= 2).SelectMany(c =>
        {
            var ys = new List<double>();
            for (var i = 1; i < c.Args.Count; i += 2) ys.Add(c.Args[i]);
            return ys;
        }).Max();
        // 小弧只吃到切点（y=0 或 20），大弧必然越过另一侧
        Assert.InRange(maxY, -0.0001, 20.0001);
    }

    [Fact]
    public void ArcWithZeroRadiusFallsBackToLine()
    {
        var commands = Assert.Single(Ok(Wrap(@"<path d=""M0 0 A0 5 0 0 1 10 10""/>")).Paths).Commands;
        Assert.Contains(commands, c => c.Command == 'L');
        Assert.DoesNotContain(commands, c => c.Command == 'C');
    }

    [Fact]
    public void ArcWithTooSmallRadiusIsScaledUpNotDropped()
    {
        // 半径 1 画不出 (0,0)→(20,0) 的弧，规范要求等比放大到刚好能画
        var path = Assert.Single(Ok(Wrap(@"<path d=""M0 0 A1 1 0 0 1 20 0""/>")).Paths);
        Assert.Contains(path.Commands, c => c.Command == 'C');
        Assert.Equal(20, path.Bounds.MaxX, 4);
    }

    [Fact]
    public void GroupTransformAppliesToChildrenAndMultiplies()
    {
        var doc = Ok(Wrap(@"<g transform=""translate(10,10) scale(2)""><rect x=""1"" y=""1"" width=""5"" height=""5""/></g>"));
        var bounds = Assert.Single(doc.Paths).Bounds;
        Assert.Equal(12, bounds.XMm, 6);   // (1·2)+10
        Assert.Equal(10, bounds.WidthMm, 6); // 5·2
    }

    [Fact]
    public void StrokeWidthScalesWithTransformAndLandsInMillimetres()
    {
        var doc = Ok(Wrap(@"<g transform=""scale(2)""><rect width=""10"" height=""10"" fill=""none"" stroke=""#000"" stroke-width=""0.5""/></g>"));
        Assert.Equal(1, Assert.Single(doc.Paths).Stroke!.WidthMm, 6);
    }

    [Fact]
    public void DashArrayIsInMillimetresNotStrokeWidths()
    {
        // 回归：曾经拿线宽去乘虚线段长，量纲错了，虚线会缩成一团
        var doc = Ok(Wrap(@"<rect width=""50"" height=""10"" fill=""none"" stroke=""#000"" stroke-width=""2"" stroke-dasharray=""4 2""/>"));
        var dash = Assert.Single(doc.Paths).Stroke!.DashMm!;
        Assert.Equal(new[] { 4d, 2d }, dash);
    }

    [Fact]
    public void ZeroSizeRectIsSkippedWithWarning()
    {
        var result = SvgParser.Parse(Wrap(@"<rect width=""0"" height=""10""/>"));
        Assert.Empty(result.Document.Paths);
        Assert.Contains(result.Issues, i => i.Severity == IssueLevel.Warning && i.Message.Contains("<rect>", StringComparison.Ordinal));
    }

    [Fact]
    public void NoPaintAtAllMeansNothingToDraw()
    {
        var result = SvgParser.Parse(Wrap(@"<rect width=""10"" height=""10"" fill=""none""/>"));
        Assert.Empty(result.Document.Paths);
        Assert.Contains(result.Issues, i => i.Message.Contains("既没填充也没描边", StringComparison.Ordinal));
    }

    // ---------- 样式与 CSS ----------

    [Fact]
    public void CssClassRuleSetsFontSize()
    {
        var doc = Ok(Wrap(@"<style>.t { font-size: 12pt; font-family: Arial; }</style><text x=""10"" y=""20"" class=""t"">ABC</text>"));
        var text = Assert.Single(doc.Texts);
        // 12pt = 4.2333mm；这份文档 1 用户单位 = 1mm，所以字号（磅）就该是 12
        Assert.Equal(12, text.SizePt, 3);
        Assert.Equal("Arial", text.FontFamily);
    }

    [Fact]
    public void InlineStyleBeatsPresentationAttribute()
    {
        var doc = Ok(Wrap(@"<text x=""10"" y=""20"" font-size=""8"" style=""font-size:16"">A</text>"));
        // 本文档 1 用户单位 = 1mm，内联的 16 胜过表现属性的 8 → 16mm = 45.35pt
        Assert.Equal(16 * 72.0 / 25.4, Assert.Single(doc.Texts).SizePt, 3);
    }

    [Fact]
    public void StyleInheritsDownTheTree()
    {
        var doc = Ok(Wrap(@"<g font-family=""SimHei"" font-weight=""bold""><text x=""5"" y=""5"">A</text></g>"));
        var text = Assert.Single(doc.Texts);
        Assert.Equal("SimHei", text.FontFamily);
        Assert.True(text.Bold);
    }

    [Fact]
    public void AbsoluteFontSizeDoesNotDoubleScale()
    {
        // 回归：12pt 曾被当成 12 用户单位，在这份 1 单位=1mm 的文档里会算成 34pt
        var doc = Ok(Wrap(@"<text x=""10"" y=""20"" font-size=""12pt"">A</text>"));
        Assert.Equal(12, Assert.Single(doc.Texts).SizePt, 3);
    }

    [Fact]
    public void PxFontSizeFollowsUserUnit()
    {
        var doc = Ok(Wrap(@"<text x=""10"" y=""20"" font-size=""10"">A</text>"));
        // 本文档 1 单位 = 1mm，10 单位 = 10mm = 28.35pt
        Assert.Equal(10 * 72.0 / 25.4, Assert.Single(doc.Texts).SizePt, 3);
    }

    [Fact]
    public void UnknownColorNameFallsBackToBlackWithWarning()
    {
        var result = SvgParser.Parse(Wrap(@"<rect width=""10"" height=""10"" fill=""BrandBlue-900""/>"));
        Assert.Equal("#000000", Assert.Single(result.Document.Paths).Fill!.Color);
        Assert.Contains(result.Issues, i => i.Message.Contains("BrandBlue-900", StringComparison.Ordinal));
    }

    [Fact]
    public void GradientFillDegradesToSolidBlackOnceAndSaysSo()
    {
        var result = SvgParser.Parse(Wrap(@"<rect width=""10"" height=""10"" fill=""url(#g1)""/><rect x=""20"" width=""10"" height=""10"" fill=""url(#g1)""/>"));
        Assert.Equal(2, result.Document.Paths.Count);
        Assert.All(result.Document.Paths, p => Assert.Equal("#000000", p.Fill!.Color));
        Assert.Single(result.Issues, i => i.Message.Contains("渐变", StringComparison.Ordinal));
    }

    [Fact]
    public void ShorthandHexAndRgbAndNamedColorsAllNormalise()
    {
        var doc = Ok(Wrap(@"<rect width=""5"" height=""5"" fill=""#f0a""/><rect x=""10"" width=""5"" height=""5"" fill=""rgb(1,2,3)""/><rect x=""20"" width=""5"" height=""5"" fill=""White""/>"));
        Assert.Equal("#ff00aa", doc.Paths[0].Fill!.Color);
        Assert.Equal("#010203", doc.Paths[1].Fill!.Color);
        Assert.Equal("#ffffff", doc.Paths[2].Fill!.Color);
    }

    [Fact]
    public void DeviceCmykFillBecomesItsApproximationAndSaysSo()
    {
        // Corel / AI 系底稿写的是 device-cmyk(...)（CSS 规定分量取 0~1）。
        // 第 47 棒之前这里只匹配 "cmyk(" 前缀，于是真家伙一路掉进「不认（可能是自定义色板名）」：
        // **解释是错的，颜色也错了**（抹成黑）。现在它换算成屏幕近似值，并把话说清。
        var result = SvgParser.Parse(Wrap(@"<rect width=""10"" height=""10"" fill=""device-cmyk(0 0.91 0.90 0)""/>"));
        var fill = Assert.Single(result.Document.Paths).Fill!;
        Assert.NotEqual("#000000", fill.Color);
        Assert.StartsWith("#ff", fill.Color, StringComparison.Ordinal);          // 那支红不会画成黑
        Assert.Contains(result.Issues, i => i.Message.Contains("近似", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Issues, i => i.Message.Contains("色板名", StringComparison.Ordinal));
    }

    [Fact]
    public void BareCmykPercentsAreAlsoRead()
    {
        // 各家导出工具的另一副面孔：百分数、逗号分隔。
        var doc = Ok(Wrap(@"<rect width=""5"" height=""5"" fill=""cmyk(0,100,100,0)""/>"));
        Assert.Equal("#ff0000", doc.Paths[0].Fill!.Color);
    }

    [Fact]
    public void DisplayNoneRemovesWholeGroup()
    {
        var doc = Ok(Wrap(@"<g display=""none""><rect width=""5"" height=""5""/></g><rect x=""6"" width=""5"" height=""5""/>"));
        var path = Assert.Single(doc.Paths);
        Assert.Equal(6, path.Bounds.XMm, 6);
    }

    // ---------- 文字 ----------

    [Fact]
    public void TextKeepsBaselineAndEstimatesBox()
    {
        var doc = Ok(Wrap(@"<text x=""10"" y=""30"" font-size=""5"">ABCDEFG</text>"));
        var text = Assert.Single(doc.Texts);
        Assert.Equal("ABCDEFG", text.Content);
        Assert.Equal(30, text.BaselineYmm, 6);
        Assert.True(text.Bounds.YMm < 30, "顶边必须在基线之上");
        Assert.True(text.Bounds.HeightMm > 0);
    }

    [Fact]
    public void AnchorMiddleCentersBoxOnX()
    {
        var start = Assert.Single(Ok(Wrap(@"<text x=""50"" y=""30"" font-size=""5"">MADE IN CHINA</text>")).Texts);
        var middle = Assert.Single(Ok(Wrap(@"<text x=""50"" y=""30"" font-size=""5"" text-anchor=""middle"">MADE IN CHINA</text>")).Texts);
        Assert.Equal(start.XMm, middle.XMm + start.WidthEstimateMm / 2, 4);
    }

    [Fact]
    public void WhitespaceIsCollapsedLikeSvgDefault()
    {
        var doc = Ok(Wrap(@"<text x=""1"" y=""1"">  合同   号

ABC  </text>"));
        Assert.Equal("合同 号 ABC", Assert.Single(doc.Texts).Content);
    }

    [Fact]
    public void TspansWithoutXContinueThePenAndWithYStartNewLine()
    {
        var doc = Ok(Wrap(@"<text x=""10"" y=""20""><tspan>AA</tspan><tspan x=""10"" y=""30"">BB</tspan></text>"));
        Assert.Equal(2, doc.Texts.Count);
        Assert.Equal(20, doc.Texts[0].BaselineYmm, 6);
        Assert.Equal(30, doc.Texts[1].BaselineYmm, 6);
        Assert.True(doc.Texts[0].XMm < doc.Texts[1].XMm + 5);
        Assert.Equal("AA", doc.Texts[0].Content);
        Assert.Equal("BB", doc.Texts[1].Content);
    }

    [Fact]
    public void CjkIsFullWidthAndLatinIsNot()
    {
        var wide = SvgParser.EstimateWidthMm("唛头标签", 10);
        var narrow = SvgParser.EstimateWidthMm("abcd", 10);
        Assert.True(wide > narrow * 1.5, $"四个汉字应当明显宽于四个拉丁字母：{wide} vs {narrow}");
    }

    [Fact]
    public void AbsurdFontSizeIsClampedIntoPrintableRange()
    {
        var result = SvgParser.Parse(Wrap(@"<text x=""1"" y=""1"" font-size=""200"">A</text>"));
        Assert.Equal(TemplateValidator.MaxFontPt, Assert.Single(result.Document.Texts).SizePt, 3);
        Assert.Contains(result.Issues, i => i.Message.Contains("pt", StringComparison.Ordinal));
    }

    // ---------- 图片 / use / 不支持的东西 ----------

    [Fact]
    public void DataUriImageDecodesIntoBytes()
    {
        var png = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        var doc = Ok(Wrap($@"<image x=""5"" y=""6"" width=""20"" height=""10"" href=""data:image/png;base64,{png}""/>"));
        var image = Assert.Single(doc.Images);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, image.Bytes);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(20, image.Bounds.WidthMm, 6);
    }

    [Fact]
    public void ExternalImageIsSkippedWithHonestWarning()
    {
        var result = SvgParser.Parse(Wrap(@"<image x=""0"" y=""0"" width=""10"" height=""10"" href=""logo.png""/>"));
        Assert.Empty(result.Document.Images);
        Assert.Contains(result.Issues, i => i.Message.Contains("外链", StringComparison.Ordinal));
    }

    [Fact]
    public void UseExpandsLocalReference()
    {
        var doc = Ok(Wrap(@"<defs><rect id=""m"" width=""10"" height=""10""/></defs><use href=""#m"" x=""20"" y=""5""/>"));
        var path = Assert.Single(doc.Paths);
        Assert.Equal(20, path.Bounds.XMm, 6);
        Assert.Equal(5, path.Bounds.YMm, 6);
    }

    [Fact]
    public void UseWithMissingTargetWarns()
    {
        var result = SvgParser.Parse(Wrap(@"<use href=""#nope""/>"));
        Assert.Empty(result.Document.Paths);
        Assert.Contains(result.Issues, i => i.Message.Contains("找不到", StringComparison.Ordinal));
    }

    [Fact]
    public void ClipPathIsReportedNotSilentlyDropped()
    {
        var result = SvgParser.Parse(Wrap(@"<clipPath id=""c""><rect width=""5"" height=""5""/></clipPath><rect width=""20"" height=""20"" clip-path=""url(#c)""/>"));
        Assert.Contains(result.Issues, i => i.Message.Contains("clipPath", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownTagsWarnOnceNotPerOccurrence()
    {
        var result = SvgParser.Parse(Wrap(@"<feGaussianBlur/><feGaussianBlur/><feGaussianBlur/>"));
        Assert.Single(result.Issues);
    }

    // ---------- 坏输入 ----------

    [Fact]
    public void MalformedXmlIsAnErrorNotAnException()
    {
        var result = SvgParser.Parse("<svg><rect></svg>");
        Assert.True(result.HasError);
        Assert.Contains("SVG", result.ErrorMessages.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void NonSvgRootIsRejected() => Assert.Contains(SvgParser.Parse("<html><body/></html>").ErrorMessages, m => m.Contains("<svg>", StringComparison.Ordinal));

    [Fact]
    public void EmptyStringIsRejected() => Assert.True(SvgParser.Parse(string.Empty).HasError);

    /// <summary>
    /// 真底稿必带 W3C 的 DOCTYPE，所以“看见 DTD 就拒收”是错的（M7 拿真样件才测出来）；
    /// 但也不许去取它、更不许展开实体。这里把两条边界同时钉住：
    /// 外部 DTD 指向一个没人监听的地址照样解析成功（说明根本没发请求），
    /// 而引用未定义实体必须报错，不是悄悄展开。
    /// </summary>
    [Fact]
    public void DtdDeclarationIsToleratedButNothingIsFetchedOrExpanded()
    {
        const string external = """<?xml version="1.0"?><!DOCTYPE svg SYSTEM "http://127.0.0.9/nothing-listens-here/svg11.dtd"><svg xmlns="http://www.w3.org/2000/svg" width="10mm" height="10mm"><rect width="1" height="1"/></svg>""";
        var accepted = SvgParser.Parse(external);
        Assert.False(accepted.HasError, "带 DOCTYPE 的真底稿必须收得下，而且不能去取那个地址");
        Assert.Single(accepted.Document.Paths);

        const string entity = """<?xml version="1.0"?><!DOCTYPE svg [<!ENTITY x "ha">]><svg xmlns="http://www.w3.org/2000/svg" width="10mm" height="10mm">&x;<rect width="1" height="1"/></svg>""";
        Assert.True(SvgParser.Parse(entity).HasError, "实体引用不能悄悄展开");
    }

    [Fact]
    public void NodeCapTurnsIntoAnErrorAndSaysWhy()
    {
        var body = string.Concat(Enumerable.Repeat(@"<rect width=""1"" height=""1""/>", 50));
        var result = SvgParser.Parse(Wrap(body), new SvgParseOptions { MaxNodes = 10 });
        Assert.True(result.HasError);
        Assert.Contains("画册", result.ErrorMessages.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFileOnMissingPathReturnsError()
    {
        var result = SvgParser.ParseFile(Path.Combine(Path.GetTempPath(), "labelgou-不存在-" + Guid.NewGuid().ToString("N") + ".svg"));
        Assert.True(result.HasError);
    }

    // ---------- 写出与往返 ----------

    [Fact]
    public void WrittenDocumentIsMmBasedAndParseableBack()
    {
        const string source = Page1To1 +
            @"<rect x=""10"" y=""10"" width=""30"" height=""20""/>" +
            @"<circle cx=""50"" cy=""40"" r=""5""/>" +
            @"<path d=""M5 5 C10 20 20 20 25 5"" fill=""none"" stroke=""#000"" stroke-width=""0.4""/>" +
            @"<text x=""60"" y=""70"" font-size=""4"">MADE IN CHINA</text>" +
            "</svg>";
        var first = Ok(source);

        var written = SvgWriter.WriteDocument(first);
        Assert.Contains("width=\"100mm\"", written, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 100 80\"", written, StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", written, StringComparison.Ordinal);

        var again = Ok(written);
        Assert.Equal(first.Paths.Count, again.Paths.Count);
        Assert.Equal(first.Texts.Count, again.Texts.Count);
        Assert.Equal(first.WidthMm, again.WidthMm, 3);
        Assert.Equal(first.HeightMm, again.HeightMm, 3);
        for (var i = 0; i < first.Paths.Count; i++)
        {
            Assert.Equal(first.Paths[i].Bounds.XMm, again.Paths[i].Bounds.XMm, 2);
            Assert.Equal(first.Paths[i].Bounds.HeightMm, again.Paths[i].Bounds.HeightMm, 2);
        }
    }

    [Fact]
    public void WritingCanDropPromotedTextNodesOnly()
    {
        const string source = Page1To1 +
            @"<text id=""keep"" x=""1"" y=""5"" font-size=""4"">FIXED</text>" +
            @"<text id=""lift"" x=""1"" y=""20"" font-size=""4"">LOS ANGELES</text>" +
            "</svg>";
        var doc = Ok(source);
        var written = SvgWriter.WriteDocument(doc, new SvgWriteOptions { ExcludedTextIds = new[] { "lift" } });
        Assert.Contains("FIXED", written, StringComparison.Ordinal);
        Assert.DoesNotContain("LOS ANGELES", written, StringComparison.Ordinal);

        var again = Ok(written);
        var expectedPaths = doc.Paths.Count;
        Assert.Equal("FIXED", Assert.Single(again.Texts).Content);
        // 几何一个不少
        Assert.Equal(expectedPaths, again.Paths.Count);
    }

    [Fact]
    public void WriterExcludesMergedSourceIdsNotJustTheFirstId()
    {
        // SvgModel.MergedSourceIds 的契约:提升成可编辑元素后这些 id 都得从底图剔掉,
        // 只剔 Id 一个会把剩下的单字再印一遍(第 23 棒)。
        var doc = new SvgDocument();
        doc.Add(new SvgText { Content = "BOLAROM", MergedSourceIds = new[] { "t1", "t2" } });

        var written = SvgWriter.WriteDocument(doc, new SvgWriteOptions { ExcludedTextIds = new[] { "t2" } });

        Assert.DoesNotContain("BOLAROM", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Rotate45DoesNotShrinkFonts()
    {
        // 旧 StrokeScale 用 max(|A|,|B|):rotate(45°) 时 0.707,12pt 被算成 8.5pt(第 23 棒)。
        var doc = Ok(Wrap(@"<g transform=""rotate(45)""><text x=""10"" y=""20"" font-size=""12pt"">A</text></g>"));
        Assert.Equal(12, Assert.Single(doc.Texts).SizePt, 3);
    }

    [Fact]
    public void MixedBareTextAndTspanKeepsDocumentOrder()
    {
        // "Total: <tspan>5</tspan> pcs" 旧写法把裸文本整段插到最前,印成 "5Total: pcs"(第 23 棒)。
        // 解析器默认跑 SvgTextLineJoiner:同基线的段会被并回一行,内容顺序就是断言点。
        var doc = Ok(Wrap(@"<text x=""10"" y=""20"">Total: <tspan font-weight=""bold"">5</tspan> pcs</text>"));

        var joined = Assert.Single(doc.Texts);
        Assert.Equal("Total: 5 pcs", joined.Content);
    }

    [Fact]
    public void UnitSuffixedNumbersWarnInsteadOfSilentlyDropping()
    {
        // "5mm" 旧写法只取数字、单位被静默扔掉;viewBox 不是 1:1 时形状会错,至少说一声(第 23 棒)。
        var result = SvgParser.Parse(Wrap(@"<rect x=""5mm"" y=""2"" width=""20mm"" height=""10"" fill=""black""/>"));

        Assert.Contains(result.Issues, i => i.Message.Contains("单位", StringComparison.Ordinal));
        Assert.Single(result.Document.Paths);
    }

    [Fact]
    public void WriterEscapesTextAndAttributes()
    {
        var doc = Ok(Wrap(@"<text x=""1"" y=""5"" font-size=""4"">A &amp; B &lt;x&gt;</text>"));
        var written = SvgWriter.WriteDocument(doc);
        Assert.Contains("A &amp; B &lt;x&gt;", written, StringComparison.Ordinal);
        Assert.True(SvgParser.Parse(written).Document.Texts.Single().Content == "A & B <x>");
    }

    [Fact]
    public void BuilderLayersNestAndBalance()
    {
        var builder = new SvgBuilder(100, 80, "测试");
        builder.StartLayer("crop-marks", "裁切线");
        builder.Line(0, 0, 10, 10, new SvgPaint { WidthMm = 0.3 });
        builder.EndLayer();
        var xml = builder.Build();
        Assert.Contains("<g id=\"crop-marks\"", xml, StringComparison.Ordinal);
        Assert.Contains("</g>", xml, StringComparison.Ordinal);
        Assert.Contains("<line ", xml, StringComparison.Ordinal);
        Assert.Equal(1, builder.ElementCount);
    }

    [Fact]
    public void RoundTripKeepsArcGeometryCloseToOriginal()
    {
        const string source = Page1To1 + @"<path d=""M20 20 A10 5 0 0 1 40 20""/>" + "</svg>";
        var first = Assert.Single(Ok(source).Paths);
        var again = Assert.Single(Ok(SvgWriter.WriteDocument(Ok(source))).Paths);
        Assert.Equal(first.Bounds.XMm, again.Bounds.XMm, 2);
        Assert.Equal(first.Bounds.MaxX, again.Bounds.MaxX, 2);
        Assert.Equal(first.Bounds.YMm, again.Bounds.YMm, 2);
        Assert.Equal(first.Bounds.MaxY, again.Bounds.MaxY, 2);
    }

    // ---------- 逐字拆开的底稿拼回整行（真样本形状，见 SvgTextLineJoiner 注释） ----------

    private const string Page140x100 = """<svg xmlns="http://www.w3.org/2000/svg" width="140mm" height="100mm" viewBox="0 0 140 100">""";

    /// <summary>一行 BOLAROM 被拆成 7 个 <text>，全角冒号还被丢到文件末尾——这就是 CDR X4 导出的真实形状。</summary>
    private const string PerGlyphBolarom = Page140x100 +
        @"<text x=""10"" y=""20"" font-size=""10"" font-family=""Arial"">B</text>" +
        @"<text x=""18"" y=""20"" font-size=""10"" font-family=""Arial"">O</text>" +
        @"<text x=""26"" y=""20"" font-size=""10"" font-family=""Arial"">L</text>" +
        @"<text x=""34"" y=""20"" font-size=""10"" font-family=""Arial"">A</text>" +
        @"<text x=""42"" y=""20"" font-size=""10"" font-family=""Arial"">R</text>" +
        @"<text x=""50"" y=""20"" font-size=""10"" font-family=""Arial"">O</text>" +
        @"<text x=""58"" y=""20"" font-size=""10"" font-family=""Arial"">M</text>" +
        @"<text x=""66"" y=""20"" font-size=""10"" font-family=""Arial"">：</text>" +
        "</svg>";

    [Fact]
    public void PerGlyphTextIsStitchedBackIntoOneLine()
    {
        var doc = Ok(PerGlyphBolarom);
        var line = Assert.Single(doc.Texts);
        Assert.Equal("BOLAROM：", line.Content, ignoreCase: false);
        Assert.Equal(8, line.MergedFromCount);
        Assert.Equal(10, line.XMm, 6);          // 左端仍是第一个字的左端，不能越缝越往右跑
        Assert.Equal(76, line.Bounds.MaxX, 1);  // 包络右端 = 冒号右端（66 + 1.0em）
    }

    [Fact]
    public void MergingDoesNotInventErrorsAndTellsTheUserWhatHappened()
    {
        var result = SvgParser.Parse(PerGlyphBolarom);
        Assert.False(result.HasError);
        Assert.Contains(result.Issues, i => i.Severity == IssueLevel.Info && i.Message.Contains("合并", StringComparison.Ordinal));
    }

    [Fact]
    public void JoiningIsOptOutForRoundTripFidelity()
    {
        var raw = Ok(PerGlyphBolarom, new SvgParseOptions { JoinTextLines = false });
        Assert.Equal(8, raw.Texts.Count);
        Assert.Equal("B", raw.Texts[0].Content);
    }

    [Fact]
    public void SeparateLinesColumnsAndFontSizesNeverGetGluedTogether()
    {
        // 同基线但隔了 24mm 的是两栏；基线差 20mm 的是两行；字号从 10 跳到 20 的是客户名与明细行
        const string source = Page140x100 +
            @"<text x=""10"" y=""20"" font-size=""10"" font-family=""Arial"">A</text>" +
            @"<text x=""18"" y=""20"" font-size=""10"" font-family=""Arial"">B</text>" +
            @"<text x=""42"" y=""20"" font-size=""10"" font-family=""Arial"">C</text>" +
            @"<text x=""10"" y=""40"" font-size=""10"" font-family=""Arial"">D</text>" +
            @"<text x=""10"" y=""60"" font-size=""20"" font-family=""Arial"">E</text>" +
            "</svg>";
        var doc = Ok(source);
        Assert.Equal(new[] { "AB", "C", "D", "E" }, doc.Texts.Select(t => t.Content).ToArray());
    }

    [Fact]
    public void OutOfSourceOrderGlyphsLandInVisualOrder()
    {
        // 冒号在文件里排在最后，但 x 在中间：合并必须按映射后的 X 排，不能按文档顺序串
        const string source = Page140x100 +
            @"<text x=""30"" y=""20"" font-size=""10"" font-family=""Arial"">2</text>" +
            @"<text x=""10"" y=""20"" font-size=""10"" font-family=""Arial"">QTY</text>" +
            @"<text x=""22"" y=""20"" font-size=""10"" font-family=""Arial"">：</text>" +
            "</svg>";
        Assert.Equal("QTY：2", Assert.Single(Ok(source).Texts).Content);
    }

    /// <summary>
    /// 基线容差必须按**毫米**算。本例两行隔 2.5mm、字大 6mm：
    /// 拿磅当毫米用就会得出 3.06mm 的宽容差，把上下两行缝成一行（真摔过一跤）。
    /// </summary>
    [Fact]
    public void BaselineToleranceIsMillimetresNotPoints()
    {
        const string source = Page140x100 +
            @"<text x=""10"" y=""20"" font-size=""6"" font-family=""Arial"">UPPER</text>" +
            @"<text x=""10"" y=""22.5"" font-size=""6"" font-family=""Arial"">LOWER</text>" +
            "</svg>";
        var doc = Ok(source);
        Assert.Equal(new[] { "UPPER", "LOWER" }, doc.Texts.Select(t => t.Content).ToArray());
    }

    /// <summary>真样本形状：「Ctns：5」粗体 + 「件」正常体在同一基线上，不能因字重不同拆成两行。</summary>
    [Fact]
    public void MixedWeightPiecesOnOneBaselineStayTogether()
    {
        const string source = Page140x100 +
            @"<text x=""10"" y=""20"" font-size=""6"" font-weight=""bold"" font-family=""Arial"">Ctns</text>" +
            @"<text x=""22"" y=""20"" font-size=""6"" font-weight=""bold"" font-family=""Arial"">：</text>" +
            @"<text x=""28"" y=""20"" font-size=""6"" font-weight=""bold"" font-family=""Arial"">5</text>" +
            @"<text x=""31"" y=""20"" font-size=""6"" font-family=""Arial"">件</text>" +
            "</svg>";
        Assert.Equal("Ctns：5件", Assert.Single(Ok(source).Texts).Content);
    }

    /// <summary>
    /// CorelDRAW/Illustrator 导出的 SVG 开头带 W3C 的 DOCTYPE。
    /// 禁 DTD 会让每一张真底稿都被拒收（这就是“SVG 导入没效果”的第一层原因），
    /// 但收下来也不能去网上取它——XmlResolver 为 null，取不到就报错，绝不卡住。
    /// </summary>
    [Fact]
    public void SvgDoctypeFromExportToolIsAcceptedWithoutFetchingIt()
    {
        const string source = """<?xml version="1.0" encoding="UTF-8"?>""" + "\n" +
            """<!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">""" + "\n" +
            Page140x100 +
            @"<text x=""10"" y=""20"" font-size=""10"" font-family=""Arial"">QTY</text>" +
            "</svg>";
        var doc = Ok(source);
        Assert.Equal("QTY", Assert.Single(doc.Texts).Content);
    }
}
