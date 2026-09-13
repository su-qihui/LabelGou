using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using LabelGou.App.Export;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Colors;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 50 棒第二刀：矩形的填充 / 描边 / 逐角圆角。
/// <para>钉的是三件事：① 填充与笔色是<strong>两支各自独立的墨</strong>（面板上一组控件、两个目标，
/// 关掉开关要回到"缺字段"的老形状）；② 圆角在<strong>五个出口是同一个形状</strong>——SVG 那条路以前少了
/// "沿边走到起弧点"那条直线，直边会被整条拽弯，这一族错（屏上弯、纸外直）已由 <see cref="TheSvgPathAndTheWpfGeometryRoundTheSameCorners"/> 永久看住；
/// ③ 关掉描边又不填充那种"纸上什么都不剩"的矩形，要说话，不许默默消失。</para>
/// </summary>
public sealed class RectAppearanceTests : IDisposable
{
    private readonly string _dir;

    public RectAppearanceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "labelgou-rect-appearance-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录留给人看一次就够了 */ }
    }

    private const double LabelW = 60;
    private const double LabelH = 30;

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static SheetSpec Spec() => new()
    {
        Id = "test.rect", Name = "矩形小样",
        PaperWidthMm = 80, PaperHeightMm = 40,
        MarginLeftMm = 4, MarginTopMm = 4, MarginRightMm = 4, MarginBottomMm = 4,
        GutterXMm = 2, GutterYMm = 2, CropMarkThicknessMm = 0.15,
        RepeatSameLabelPerPage = false,
    };

    private static LabelTemplate TemplateOf(TemplateElement element)
    {
        var template = new LabelTemplate
        {
            Id = "user.rect", Name = "矩形外观", WidthMm = LabelW, HeightMm = LabelH, PaddingMm = 2, BorderMm = 0,
        };
        template.Elements.Add(element);
        return template;
    }

    private static TemplateElement Box(double x = 10, double y = 6, double w = 40, double h = 16) => new()
    {
        Kind = ElementKind.Rect, X = x, Y = y, Width = w, Height = h,
    };

    private (TemplateEditorViewModel Vm, EditableElement Panel, TemplateElement Element) Open(TemplateElement element)
    {
        var vm = new TemplateEditorViewModel(TemplateOf(element), new TemplateStore(_dir));
        vm.SelectedRow = vm.Elements[0];
        var panel = vm.Editing ?? throw new InvalidOperationException("选中行没建起来，面板不成立");
        return (vm, panel, vm.Template.Elements[0]);
    }

    private static string SvgOf(TemplateElement element)
    {
        var request = new SheetExportRequest
        {
            Plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1),
            Source = new PageContentSource(TemplateOf(element), new List<MarkRecord> { SampleRecords.StandardSample() }, "样例.xlsx"),
            PageIndexes = new List<int> { 0 },
            BaseName = "矩形件",
        };
        return Assert.Single(SheetSvgWriter.WritePage(request, 0, SvgExportOptions.Default)).Xml;
    }

    /// <summary>取那条圆角矩形的路径：起点唯一（x+左上半径），不会跟裁切角线或文字轮廓撞上。</summary>
    private static string RectPathOf(string xml, string startsWith)
        => XDocument.Parse(xml).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Select(e => e.Attribute("d")!.Value)
            .Single(d => d.StartsWith(startsWith, StringComparison.Ordinal));

    // ---------- 面板：两支墨 ----------

    [Fact]
    public void TheColourControlsFollowTheSwitchBetweenPenAndFill() => OnSta(() =>
    {
        var (_, panel, element) = Open(Box());

        Assert.True(panel.FillShowsSlash);                                    // 没填＝斜杠「无颜色」块，不是黑块配勾选框
        Assert.Equal("#ffffff", Hex(panel.FillSwatch));                       // 块底是白的——红杠画在它上面；画黑就是拿"黑"谎称"有填充"
        Assert.Equal("黑（默认）", panel.InkSummary);

        panel.OpenFillEditorCommand.Execute(null);                            // 点开填充那颗＝面板指到填充（补正四起没有"开填充"勾选框）
        Assert.True(panel.EditingFill);

        // 四格仍是 47 棒那四个各自独立的数：从黑起手时 K=100，动了 C 不会自己归零。这里要钉的不是算式，是落点。
        panel.InkC = 100;
        panel.InkK = 0;
        Assert.NotNull(element.FillColor);                                    // 选了一支墨＝填充有了
        Assert.Equal((100, 0, 0, 0), (element.FillColor!.C, element.FillColor.M, element.FillColor.Y, element.FillColor.K));
        Assert.Null(element.InkColor);                                        // 笔色没被碰过

        // 填充编辑中按"恢复默认"：字段清空，这一格要说"不填充"，不能谎称是黑；目标也不许偷偷跳回笔色。
        panel.ResetInkCommand.Execute(null);
        Assert.Null(element.FillColor);
        Assert.Equal("不填充", panel.InkSummary);
        Assert.True(panel.FillShowsSlash);                                    // 色块自己变回斜杠块
        Assert.True(panel.EditingFill);

        panel.EditingFill = false;                                            // 切回笔色：同一组控件改的是另一个字段
        Assert.Equal(0, panel.InkC);                                          // 面板数字跟着换目标，不许把填充那支的数留在笔色格上骗人
        panel.InkM = 100;
        Assert.Equal(100, element.InkColor!.M);
        Assert.Equal(0, element.InkColor.C);
        return true;
    });

    [Fact]
    public void TheTwoSwatchesEachShowTheirOwnInkWhateverThePopupIsEditing() => OnSta(() =>
    {
        var (_, panel, element) = Open(Box());
        panel.OpenFillEditorCommand.Execute(null);                        // 补正四：填充没有勾选框了，点开色块挑一支墨就是上色
        panel.InkC = 100;
        panel.InkK = 0;                                             // 填色＝纯青
        panel.EditingFill = false;
        panel.InkM = 100;
        panel.InkK = 0;                                             // 笔色＝纯品红

        // 两颗色块各看各的：从哪颗进来编辑都不许把另一颗也刷成同一个颜色
        Assert.Equal("#ff00ff", Hex(panel.PenSwatch));
        Assert.Equal("#00ffff", Hex(panel.FillSwatch));
        Assert.Contains("#ff00ff", panel.PenSummary, StringComparison.Ordinal);
        Assert.Contains("#00ffff", panel.FillSummary, StringComparison.Ordinal);
        Assert.True(element.InkColor!.M == 100 && element.FillColor!.C == 100);
        return true;
    });

    [Fact]
    public void EachSwatchOpensTheOneSharedEditorPointedAtItself() => OnSta(() =>
    {
        var (_, panel, _) = Open(Box());

        panel.OpenFillEditorCommand.Execute(null);
        Assert.True(panel.InkPopupOpen);
        Assert.True(panel.EditingFill);                             // 从填充那颗进来＝这一组控件改填充
        Assert.Equal("正在编辑：填充", panel.InkTargetText);

        panel.OpenPenEditorCommand.Execute(null);
        Assert.True(panel.InkPopupOpen);                            // 弹层不关，只换目标
        Assert.False(panel.EditingFill);
        panel.InkC = 100;                                           // 这一路落笔色，不碰填充
        Assert.Equal(100, panel.Element.InkColor!.C);
        Assert.Null(panel.Element.FillColor);
        return true;
    });

    [Fact]
    public void TheNoColourSwatchTurnsOffWhicheverInkIsBeingEdited() => OnSta(() =>
    {
        var (_, panel, element) = Open(Box());

        panel.OpenFillEditorCommand.Execute(null);
        Assert.False(panel.NoInkCommand.CanExecute(null));                    // 本来就没填充：不给一颗点了没反应的按钮
        panel.InkC = 100;                                                     // 补正四：选一支墨＝填充有了（没有勾选框了）
        Assert.True(panel.NoInkCommand.CanExecute(null));
        panel.NoInkCommand.Execute(null);
        Assert.Null(element.FillColor);                                       // 关掉的是填充
        Assert.True(element.ShowsStroke);                                     // 描边那支一个字没被碰

        panel.OpenPenEditorCommand.Execute(null);
        Assert.True(panel.NoInkCommand.CanExecute(null));                     // 描边还开着，能关
        panel.NoInkCommand.Execute(null);
        Assert.False(element.ShowsStroke);
        Assert.True(panel.PenShowsSlash);                                     // 关掉后笔色那颗就是斜杠块
        Assert.Equal("不描边", panel.PenSummary);
        Assert.Null(element.FillColor);
        Assert.False(panel.NoInkCommand.CanExecute(null));                    // 已经关了，灰掉

        panel.InkC = 100;                                                     // 关着的描边又选了一支墨＝重新画边框（CorelDRAW 对 X 掉的线框盒选色就是这个行为）
        Assert.True(element.ShowsStroke);
        Assert.False(panel.PenShowsSlash);
        Assert.Equal(100, element.InkColor!.C);
        return true;
    });

    [Fact]
    public void TheStrokeWidthBoxAcceptsTypedUnitsAndRefusesToQuietlyZero() => OnSta(() =>
    {
        var (_, panel, element) = Open(Box());
        Assert.Equal("0.35 mm", panel.ThicknessText);               // 默认那支笔的宽度，写法照 CorelDRAW 的"5.0 mm"

        panel.ThicknessText = "0.8";
        Assert.Equal(0.8d, element.ThicknessMm);
        panel.ThicknessText = "1.25 mm";
        Assert.Equal(1.25d, element.ThicknessMm);
        panel.ThicknessText = "2 毫米";
        Assert.Equal(2d, element.ThicknessMm);

        panel.ThicknessText = "糊了";                                // 认不出：退回上一个数，绝不静默清成 0
        Assert.Equal(2d, element.ThicknessMm);
        Assert.Equal("2 mm", panel.ThicknessText);
        panel.ThicknessText = "";
        Assert.Equal(2d, element.ThicknessMm);
        return true;
    });

    [Fact]
    public void StrokeOffIsTheOnlyThingWrittenAndComesOffAgainCleanly() => OnSta(() =>
    {
        var plain = TemplateStore.ToJson(TemplateOf(Box()));
        Assert.DoesNotContain("\"stroked\"", plain, StringComparison.Ordinal);   // 开着不写：老文件的字节数不变
        Assert.DoesNotContain("\"fillColor\"", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("\"cornerRadiusMm\"", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("\"roundedCorners\"", plain, StringComparison.Ordinal);

        var off = Box();
        off.Stroked = false;
        var json = TemplateStore.ToJson(TemplateOf(off));
        Assert.Contains("\"stroked\": false", json, StringComparison.Ordinal);

        var (vm, panel, element) = Open(off);
        Assert.False(panel.ShowsStroke);
        panel.ShowsStroke = true;
        Assert.Null(element.Stroked);                                         // 关掉再打开要回到"缺字段"，不是留一个 true
        Assert.DoesNotContain("\"stroked\"", TemplateStore.ToJson(vm.Template), StringComparison.Ordinal);
        vm.Undo();
        Assert.False(vm.Template.Elements[0].ShowsStroke);                    // 一勾一撤是一步，不是一串
        return true;
    });

    [Fact]
    public void TheCornerBoxesDoWhatTheCorelDrawDockerDoes() => OnSta(() =>
    {
        var (vm, panel, element) = Open(Box());

        Assert.True(panel.CornersLinked);                                     // 默认锁上＝四个角一起圆（CorelDRAW「全部圆角」的默认）
        panel.CornerRadiusTopLeftMm = 6;                                      // 锁上改任何一格＝四格同数
        Assert.Equal((6d, 6d, 6d, 6d), element.CornerRadii());
        Assert.Equal(6d, element.CornerRadiusMm);                             // 均匀值落老字段，不多出一行数组
        Assert.Null(element.CornerRadiiMm);
        Assert.DoesNotContain("\"cornerRadiiMm\"", TemplateStore.ToJson(vm.Template), StringComparison.Ordinal);

        panel.CornersLinked = false;                                          // 开锁：四角各管各的
        Assert.Equal(6d, panel.CornerRadiusTopRightMm);                       // 数字不跳，四格仍指着元素真值
        panel.CornerRadiusTopLeftMm = 0;                                      // 只把左上归零——老字段还表达得下（半径 + 角名单），不升级为数组
        Assert.Equal((0d, 6d, 6d, 6d), element.CornerRadii());
        Assert.Equal(6d, element.CornerRadiusMm);
        Assert.Equal(Corner.TopRight | Corner.BottomRight | Corner.BottomLeft, element.CornersToRound);
        Assert.Null(element.CornerRadiiMm);

        panel.CornerRadiusTopLeftMm = 3;                                      // 真的逐角不同才进数组形态
        Assert.Equal((3d, 6d, 6d, 6d), element.CornerRadii());
        Assert.NotNull(element.CornerRadiiMm);
        var json = TemplateStore.ToJson(vm.Template);
        Assert.Contains("\"cornerRadiiMm\": [", json, StringComparison.Ordinal);

        // 写盘读回：四个数一个一个不漂。
        var path = Path.Combine(_dir, "corners.json");
        File.WriteAllText(path, json);
        var (back, issues) = new TemplateStore(_dir).ReadFile(path);
        Assert.True(back is not null, "逐角文件读不回来：" + string.Join(" | ", issues.Select(i => i.Message)));
        Assert.Equal((3d, 6d, 6d, 6d), back!.Elements[0].CornerRadii());

        // 重新锁上再改一格＝四格归一（锁是面板状态，不毁已存的逐角数——改之前看得见各角数字）。
        panel.CornersLinked = true;
        panel.CornerRadiusBottomRightMm = 8;
        Assert.Equal((8d, 8d, 8d, 8d), element.CornerRadii());
        Assert.Null(element.CornerRadiiMm);                                   // 又均匀了：折回老字段，数组收掉
        return true;
    });

    // ---------- 形状：五个出口同一个 ----------

    [Fact]
    public void OnlyTheChosenCornersAreRoundedOnTheCanvasGeometry() => OnSta(() =>
    {
        var box = new Rect(10, 12, 40, 20);
        var geometry = LabelRenderer.RoundedRect(box, 6, 0, 0, 0);            // 只圆左上（补正四：一格一个半径）

        Assert.Equal(box, geometry.Bounds);                                   // 圆角不改外接框：摆位与越界校验照旧
        Assert.False(geometry.FillContains(new Point(10.4, 12.4)));           // 左上被圆掉
        Assert.True(geometry.FillContains(new Point(49.6, 12.4)));            // 右上还是直角
        Assert.True(geometry.FillContains(new Point(49.6, 31.6)));            // 右下
        Assert.True(geometry.FillContains(new Point(10.4, 31.6)));            // 左下

        var none = LabelRenderer.RoundedRect(box, 0, 0, 0, 0);
        Assert.True(none.FillContains(new Point(10.4, 12.4)));                // 四格全 0＝直角矩形，不是"全都圆"

        // 逐角不同（6/4/2/0）：每个角各圆各的，谁也不借谁的半径。
        var mixed = LabelRenderer.RoundedRect(box, 6, 4, 2, 0);
        Assert.Equal(box, mixed.Bounds);
        Assert.False(mixed.FillContains(new Point(10.4, 12.4)));              // 左上圆 6：角区没了
        Assert.False(mixed.FillContains(new Point(49.6, 12.4)));              // 右上圆 4：也没了，但只吃掉 4mm
        Assert.True(mixed.FillContains(new Point(49.6 - 4.6, 12.4)));         // 右上弧外一点（离角 4.6mm）仍是直角区
        Assert.True(mixed.FillContains(new Point(10.4, 31.6)));               // 左下 0＝直角
        return true;
    });

    [Fact]
    public void TheSvgPathAndTheWpfGeometryRoundTheSameCorners() => OnSta(() =>
    {
        // 曾经 SVG 少了"沿边走到起弧点"那条 L：整条直边被当成弧的一部分画弯（屏上直角、纸外鼓包）。
        // 只有右上角会露出来——另外三个角的起弧点从前有独立的 L 兜着，所以专门挑右上。
        var element = Box();
        element.SetCornerRadii(0, 6, 0, 6);                                   // 右上 + 左下同圆（均匀非零值 → 仍走老字段形态）
        var d = RectPathOf(SvgOf(element), "M 10 6");

        var wanted = LabelRenderer.RoundedRect(new Rect(10, 6, 40, 16), 0, 6, 0, 6);
        var actual = Geometry.Parse(d);
        Assert.Equal(wanted.Bounds, actual.Bounds);

        var differs = new List<string>();
        for (var x = 10d; x <= 50d; x += 0.5)
        for (var y = 6d; y <= 22d; y += 0.5)
        {
            var p = new Point(x, y);
            if (wanted.FillContains(p) != actual.FillContains(p))
                differs.Add($"({x.ToString("0.#", CultureInfo.InvariantCulture)},{y.ToString("0.#", CultureInfo.InvariantCulture)})");
        }
        Assert.True(differs.Count == 0, $"{differs.Count} 个格子两边不一样，例如 {string.Join(" ", differs.Take(6))}：\n{d}");

        // 顶边必须真的直走到起弧点 (50-6, 6)，然后才弯
        Assert.Contains("L 44 6 C", d, StringComparison.Ordinal);
        return true;
    });

    /// <summary>
    /// 补正四的新形状——四角四个不同的半径——也要跨出口同一个。逐格比 <c>FillContains</c>，
    /// 判据与上一支同源：外接框两边都对不代表形状一致（§五-149 那课）。
    /// </summary>
    [Fact]
    public void FourDifferentRadiiStillMatchAcrossOutlets() => OnSta(() =>
    {
        var element = Box();
        element.SetCornerRadii(6, 4, 2, 0);                                  // 左上6 右上4 右下2 左下0：数组形态
        Assert.NotNull(element.CornerRadiiMm);
        var d = RectPathOf(SvgOf(element), "M 16 6");                        // 起点 = x + 左上半径

        var wanted = LabelRenderer.RoundedRect(new Rect(10, 6, 40, 16), 6, 4, 2, 0);
        var actual = Geometry.Parse(d);
        Assert.Equal(wanted.Bounds, actual.Bounds);

        var differs = new List<string>();
        for (var x = 10d; x <= 50d; x += 0.5)
        for (var y = 6d; y <= 22d; y += 0.5)
        {
            var p = new Point(x, y);
            if (wanted.FillContains(p) != actual.FillContains(p))
                differs.Add($"({x.ToString("0.#", CultureInfo.InvariantCulture)},{y.ToString("0.#", CultureInfo.InvariantCulture)})");
        }
        Assert.True(differs.Count == 0, $"{differs.Count} 个格子两边不一样，例如 {string.Join(" ", differs.Take(6))}：\n{d}");
        return true;
    });

    [Fact]
    public void AnOverlargeRadiusIsCappedTheSameWayEverywhere() => OnSta(() =>
    {
        var element = Box(w: 20, h: 10);
        element.SetCornerRadii(50, 50, 50, 50);                               // 远超过短边一半（四角同值 → 老字段形态）

        var issues = TemplateValidator.Validate(TemplateOf(element));
        Assert.Contains(issues, i => i.Severity == IssueLevel.Warning && i.Message.Contains("超过短边一半", StringComparison.Ordinal));

        var box = new Rect(0, 0, 20, 10);
        var capped = LabelRenderer.RoundedRect(box, 50, 50, 50, 50);
        Assert.Equal(box, capped.Bounds);
        Assert.False(capped.FillContains(new Point(0.3, 0.3)));               // 按 5mm（= 半高）画，不是把整条边啃掉
        Assert.True(capped.FillContains(new Point(10, 5)));

        var svg = SvgOf(element);
        Assert.Contains("rx=\"5\"", svg, StringComparison.Ordinal);              // 四角同圆走 <rect rx>：半径同样先夹到短边一半
        Assert.DoesNotContain("rx=\"50\"", svg, StringComparison.Ordinal);

        // 逐角超限（补正四）：只有超的那一角被点名、被夹，别角该照自己填的数画。
        var mixed = Box(w: 20, h: 10);
        mixed.SetCornerRadii(50, 2, 0, 0);
        var mixedIssues = TemplateValidator.Validate(TemplateOf(mixed));
        var warning = Assert.Single(mixedIssues, i => i.Message.Contains("超过短边一半", StringComparison.Ordinal));
        Assert.Contains("左上 50", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("右上", warning.Message, StringComparison.Ordinal);
        var mixedSvg = SvgOf(mixed);
        // 左上被夹到 5、右上照 2：两条不同的弧 ⇒ 起点 x+5，顶边直走到 30-2 才弯。
        Assert.StartsWith("M 15 6", RectPathOf(mixedSvg, "M 15 6"), StringComparison.Ordinal);
        Assert.Contains("L 28 6 C", mixedSvg, StringComparison.Ordinal);
        return true;
    });

    [Fact]
    public void FillAndStrokeLandOnDifferentPlates() => OnSta(() =>
    {
        // 填充给青、笔色给品红：两版各画各的，谁也不许顶掉谁（叠印没做，所以描边处该把填充挖空）。
        var element = Box();
        element.FillColor = LabelColor.FromCmyk(100, 0, 0, 0);
        element.InkColor = LabelColor.FromCmyk(0, 100, 0, 0);
        element.ThicknessMm = 2;

        var request = new SheetExportRequest
        {
            Plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 1),
            Source = new PageContentSource(TemplateOf(element), new List<MarkRecord> { SampleRecords.StandardSample() }, "样例.xlsx"),
            PageIndexes = new List<int> { 0 },
            BaseName = "分色矩形",
            Dpi = 150,
            CmykPlates = true,
        };
        var page = PageRasterizer.RenderCmykPage(request.Plan, 1, request.Dpi, request.Source.AsPlateProvider(), false);
        var plates = page.ToPlanes();
        Assert.Equal(4, plates.Length);

        var cyan = Darkest(plates[0]);
        var magenta = Darkest(plates[1]);
        Assert.True(cyan.Value >= 250, $"青版上找不到满青的格子（最重 {cyan.Value}）——填充没画出来");
        Assert.True(magenta.Value >= 250, $"品红版上找不到满品的格子（最重 {magenta.Value}）——描边没画出来");
        Assert.True(plates[1][cyan.Index] <= 5, "青版最重那一格里品红不该有墨：两支墨走串了");
        Assert.True(plates[0][magenta.Index] <= 5, "品红版最重那一格里青不该有墨：描边没把填充挖空");
        Assert.True(Total(plates[2]) <= Total(plates[0]) / 50, "黄版上冒出了墨——填充或笔色被当成别的版画了");
        return true;
    });

    // ---------- 校验与老文件 ----------

    [Fact]
    public void ARectangleThatDrawsNothingSaysSo()
    {
        var element = Box();
        element.Stroked = false;
        var issues = TemplateValidator.Validate(TemplateOf(element));
        Assert.Contains(issues, i => i.Message.Contains("既不描边也不填充", StringComparison.Ordinal));

        element.FillColor = LabelColor.Black;
        Assert.DoesNotContain(TemplateValidator.Validate(TemplateOf(element)),
            i => i.Message.Contains("既不描边也不填充", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOldFileStillLoadsAndComesOutTheSameShapeItWentIn()
    {
        // v6 时代的矩形：那几件外观字段一个都没有。读进来必须等价于"有描边、不填充、直角"。
        var json = TemplateStore.ToJson(TemplateOf(Box()))
            .Replace($"\"schemaVersion\": {LabelTemplate.CurrentSchemaVersion}", "\"schemaVersion\": 6", StringComparison.Ordinal);
        var path = Path.Combine(_dir, "old.json");
        File.WriteAllText(path, json);

        var (template, issues) = new TemplateStore(_dir).ReadFile(path);
        Assert.True(template is not null, "老文件读不进来：" + string.Join(" | ", issues.Select(i => i.Message)));
        var rect = Assert.Single(template!.Elements);
        Assert.True(rect.ShowsStroke);
        Assert.Null(rect.FillColor);
        Assert.Equal((0d, 0d, 0d, 0d), rect.CornerRadii());

        Assert.DoesNotContain("\"fillColor\"", TemplateStore.ToJson(template), StringComparison.Ordinal);
        Assert.DoesNotContain("\"roundedCorners\"", TemplateStore.ToJson(template), StringComparison.Ordinal);
        Assert.DoesNotContain("\"cornerRadiiMm\"", TemplateStore.ToJson(template), StringComparison.Ordinal);
    }

    [Fact]
    public void AV8CornerFileReadsAsTheSameFourRadiiAndRoundTripsByteForByte()
    {
        // v8 的形态（补正四之前）：半径 6 + 只圆左上与右下。逐角分解必须是 (6,0,6,0)，
        // 而且不碰就存必须逐字节不变——新数组字段是"逐角真不同"才写的，读老文件不许顺手升级。
        var element = Box();
        element.CornerRadiusMm = 6;
        element.RoundedCorners = Corner.TopLeft | Corner.BottomRight;
        var written = TemplateStore.ToJson(TemplateOf(element));
        Assert.DoesNotContain("\"cornerRadiiMm\"", written, StringComparison.Ordinal);

        var path = Path.Combine(_dir, "v8.json");
        File.WriteAllText(path, written);
        var (back, issues) = new TemplateStore(_dir).ReadFile(path);
        Assert.True(back is not null, "v8 圆角文件读不回来：" + string.Join(" | ", issues.Select(i => i.Message)));
        Assert.Equal((6d, 0d, 6d, 0d), back!.Elements[0].CornerRadii());
        Assert.Equal(written, TemplateStore.ToJson(back));                    // 逐字节回环：读进来再存出去，一个字符都不动
    }

    /// <summary>画刷的 RGB 拼成小写 #rrggbb（WPF 的 Media.Color 自己没这个现成方法）。</summary>
    private static string Hex(Brush brush)
    {
        var c = ((SolidColorBrush)brush).Color;
        return $"#{c.R:x2}{c.G:x2}{c.B:x2}";
    }

    private static (int Index, int Value) Darkest(byte[] plane)
    {
        var best = 0;
        var index = 0;
        for (var i = 0; i < plane.Length; i++)
        {
            if (plane[i] <= best) continue;
            best = plane[i];
            index = i;
        }
        return (index, best);
    }

    private static long Total(byte[] plane) => plane.Sum(v => (long)v);
}
