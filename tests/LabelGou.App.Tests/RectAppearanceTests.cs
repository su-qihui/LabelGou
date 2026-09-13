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

        Assert.False(panel.FillEnabled);                                    // 没填就是没填，老模板一个字段都不多
        Assert.Equal("黑（默认）", panel.InkSummary);

        panel.FillEnabled = true;                                           // 开填充：先给一支黑，并把面板指过去
        Assert.True(panel.EditingFill);
        Assert.NotNull(element.FillColor);
        Assert.Null(element.InkColor);                                      // 笔色没被碰过

        // 四格仍是 47 棒那四个各自独立的数：从黑起手时 K=100，动了 C 不会自己归零。这里要钉的不是算式，是落点。
        panel.InkC = 100;
        panel.InkK = 0;
        Assert.Equal((100, 0, 0, 0), (element.FillColor!.C, element.FillColor.M, element.FillColor.Y, element.FillColor.K));
        Assert.Null(element.InkColor);                                      // 全程没往笔色上落一笔

        // 打开着填充又按"恢复默认"：字段清空，这一格要说"不填充"，不能谎称是黑；目标也不许偷偷跳回笔色。
        panel.ResetInkCommand.Execute(null);
        Assert.Null(element.FillColor);
        Assert.Equal("不填充", panel.InkSummary);
        Assert.True(panel.EditingFill);
        Assert.False(panel.FillEnabled);                                  // 开关照实反映"现在没填充"

        panel.FillEnabled = true;                                         // 再打开：重新给一支黑
        Assert.NotNull(element.FillColor);
        Assert.Equal((0, 0, 0, 100), (element.FillColor.C, element.FillColor.M, element.FillColor.Y, element.FillColor.K));

        panel.EditingFill = false;                                        // 切回笔色：同一组控件改的是另一个字段
        Assert.Equal(0, panel.InkC);                                      // 面板数字跟着换目标，不许把填充那支的数留在笔色格上骗人
        panel.InkM = 100;
        Assert.Equal(100, element.InkColor!.M);
        Assert.Equal(0, element.InkColor.C);
        Assert.Equal((0, 0, 0, 100), (element.FillColor!.C, element.FillColor.M, element.FillColor.Y, element.FillColor.K));
        return true;
    });

    [Fact]
    public void TheTwoSwatchesEachShowTheirOwnInkWhateverThePopupIsEditing() => OnSta(() =>
    {
        var (_, panel, element) = Open(Box());
        panel.FillEnabled = true;
        panel.EditingFill = true;
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
        panel.FillEnabled = true;
        Assert.True(panel.NoInkCommand.CanExecute(null));
        panel.NoInkCommand.Execute(null);
        Assert.Null(element.FillColor);                                       // 关掉的是填充
        Assert.True(element.ShowsStroke);                                     // 描边那支一个字没被碰

        panel.OpenPenEditorCommand.Execute(null);
        Assert.True(panel.NoInkCommand.CanExecute(null));                     // 描边还开着，能关
        panel.NoInkCommand.Execute(null);
        Assert.False(element.ShowsStroke);
        Assert.Null(element.FillColor);
        Assert.False(panel.NoInkCommand.CanExecute(null));                    // 已经关了，灰掉
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
    public void RoundingAllFourCornersWritesNothingAtAll() => OnSta(() =>
    {
        var (_, panel, element) = Open(Box());

        panel.CornerRadiusMm = 6;
        Assert.Equal(6d, element.CornerRadiusMm);
        Assert.True(panel.CornersAll);                                        // 没说圆哪几个角＝四个都圆（CorelDRAW 的默认）
        Assert.All(new[] { panel.CornerTopLeft, panel.CornerTopRight, panel.CornerBottomRight, panel.CornerBottomLeft },
            on => Assert.True(on));

        panel.CornersAll = false;
        Assert.Equal(Corner.None, element.CornersToRound);
        Assert.False(panel.CornersAll);

        panel.CornerTopLeft = true;
        panel.CornerBottomRight = true;
        Assert.Equal(Corner.TopLeft | Corner.BottomRight, element.CornersToRound);

        panel.CornerTopRight = true;
        panel.CornerBottomLeft = true;                                        // 第四颗一勾，四个角又齐了
        Assert.Null(element.RoundedCorners);                                  // 回到缺字段，不写一个"全部"占两行
        Assert.Equal(Corner.All, element.CornersToRound);
        Assert.True(panel.CornersAll);
        return true;
    });

    // ---------- 形状：五个出口同一个 ----------

    [Fact]
    public void OnlyTheChosenCornersAreRoundedOnTheCanvasGeometry() => OnSta(() =>
    {
        var box = new Rect(10, 12, 40, 20);
        var geometry = LabelRenderer.RoundedRect(box, 6, Corner.TopLeft);

        Assert.Equal(box, geometry.Bounds);                                   // 圆角不改外接框：摆位与越界校验照旧
        Assert.False(geometry.FillContains(new Point(10.4, 12.4)));           // 左上被圆掉
        Assert.True(geometry.FillContains(new Point(49.6, 12.4)));            // 右上还是直角
        Assert.True(geometry.FillContains(new Point(49.6, 31.6)));            // 右下
        Assert.True(geometry.FillContains(new Point(10.4, 31.6)));            // 左下

        var none = LabelRenderer.RoundedRect(box, 6, Corner.None);
        Assert.True(none.FillContains(new Point(10.4, 12.4)));                // 一颗角都没选＝直角矩形，不是"全都圆"
        return true;
    });

    [Fact]
    public void TheSvgPathAndTheWpfGeometryRoundTheSameCorners() => OnSta(() =>
    {
        // 曾经 SVG 少了"沿边走到起弧点"那条 L：整条直边被当成弧的一部分画弯（屏上直角、纸外鼓包）。
        // 只有右上角会露出来——另外三个角的起弧点从前有独立的 L 兜着，所以专门挑右上。
        var element = Box();
        element.CornerRadiusMm = 6;
        element.RoundedCorners = Corner.TopRight | Corner.BottomLeft;
        var d = RectPathOf(SvgOf(element), "M 10 6");

        var corners = Corner.TopRight | Corner.BottomLeft;
        var wanted = LabelRenderer.RoundedRect(new Rect(10, 6, 40, 16), 6, corners);
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

    [Fact]
    public void AnOverlargeRadiusIsCappedTheSameWayEverywhere() => OnSta(() =>
    {
        var element = Box(w: 20, h: 10);
        element.CornerRadiusMm = 50;                                          // 远超过短边一半

        var issues = TemplateValidator.Validate(TemplateOf(element));
        Assert.Contains(issues, i => i.Severity == IssueLevel.Warning && i.Message.Contains("超过短边一半", StringComparison.Ordinal));

        var box = new Rect(0, 0, 20, 10);
        var capped = LabelRenderer.RoundedRect(box, 50, Corner.All);
        Assert.Equal(box, capped.Bounds);
        Assert.False(capped.FillContains(new Point(0.3, 0.3)));               // 按 5mm（= 半高）画，不是把整条边啃掉
        Assert.True(capped.FillContains(new Point(10, 5)));

        var svg = SvgOf(element);
        Assert.Contains("rx=\"5\"", svg, StringComparison.Ordinal);              // 四角同圆走 <rect rx>：半径同样先夹到短边一半
        Assert.DoesNotContain("rx=\"50\"", svg, StringComparison.Ordinal);
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
        // v6 时代的矩形：四个新字段一个都没有。读进来必须等价于"有描边、不填充、直角"。
        var json = TemplateStore.ToJson(TemplateOf(Box()))
            .Replace("\"schemaVersion\": 8", "\"schemaVersion\": 6", StringComparison.Ordinal);
        var path = Path.Combine(_dir, "old.json");
        File.WriteAllText(path, json);

        var (template, issues) = new TemplateStore(_dir).ReadFile(path);
        Assert.True(template is not null, "老文件读不进来：" + string.Join(" | ", issues.Select(i => i.Message)));
        var rect = Assert.Single(template!.Elements);
        Assert.True(rect.ShowsStroke);
        Assert.Null(rect.FillColor);
        Assert.Equal(0d, rect.CornerRadiusMm);
        Assert.Equal(Corner.All, rect.CornersToRound);

        Assert.DoesNotContain("\"fillColor\"", TemplateStore.ToJson(template), StringComparison.Ordinal);
        Assert.DoesNotContain("\"roundedCorners\"", TemplateStore.ToJson(template), StringComparison.Ordinal);
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
