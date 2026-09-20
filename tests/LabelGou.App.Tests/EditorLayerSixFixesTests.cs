using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Editing;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 81 棒：编辑层六条（用户附截图圈出「居中时墨迹框超出实际文字位置」与「对齐=居中」那一格）。
/// <para>① 的判据刻意<strong>不拿量具比自己</strong>：参照是生产画法（<see cref="LabelRenderer"/>）画到
/// DrawingContext 上之后的几何界——"屏幕上框多大、印出来占多大"这条铁律（第 43 棒）说的就是这两者要重合。
/// 从前它是绿的假象吗？不是，它压根没被测过：旧实现把行盒当墨迹、又把折行路的对齐偏移丢了，
/// 于是框比字宽出一截还往左挪 17.9mm（用户截图里那一下）。</para>
/// </summary>
public class EditorLayerSixFixesTests
{
    private static void OnStaThread(Action work)
        => LabelGou.App.Export.StaWorker
            .RunAsync<bool>((_, _) => { work(); return true; }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static (TemplateEditorViewModel Vm, string Folder) NewEditorVm(string tag, LabelTemplate working)
    {
        var folder = Path.Combine(Path.GetTempPath(), "labelgou-b81-" + tag + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return (new TemplateEditorViewModel(working, new TemplateStore(folder)), folder);
    }

    private static TemplateEditorViewModel NewVm(string tag, LabelTemplate working) => NewEditorVm(tag, working).Vm;

    /// <summary>用户那份 AI 建议版式第一行的原数（探针 labelgou-other\_probe\b81-ink-probe.txt）。</summary>
    private static TemplateElement UsersCenteredLine(string content = "olu830-58") => new()
    {
        Kind = ElementKind.Text,
        Text = content,
        X = 6.584337,
        Y = -171.697,
        Width = 130,
        Height = 443.394,
        FontFamily = "Arial",
        FontSizePt = 59.11,
        Bold = true,
        Align = HorizontalAlign.Center,
        WrapWidthMm = 130,
        ShrinkToFit = true,
        MaxLines = 3,
        TextScaleX = 1.0551436515291936,
        TextScaleY = 2.132798963615585,
    };

    private static LabelTemplate Paper(params TemplateElement[] elements)
    {
        var template = new LabelTemplate
        {
            Id = "user.b81", Name = "编辑层六条", WidthMm = 140, HeightMm = 100, PaddingMm = 5, BorderMm = 0,
        };
        foreach (var e in elements) template.Elements.Add(e);
        return template;
    }

    /// <summary>拿生产画法渲一遍，量"这一行真的印出去的那一块"（毫米）。</summary>
    private static Rect PrintedInk(LabelTemplate template)
    {
        var layout = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1, "判据.xlsx"));
        var group = new DrawingGroup();
        using (var dc = group.Open())
            LabelRenderer.Draw(dc, layout, scale: 1.0, offsetX: 0, offsetY: 0,
                showGuides: false, pixelsPerDip: 1.0, drawBackground: false);
        var b = group.Bounds;
        return new Rect(Mm.FromDiu(b.X), Mm.FromDiu(b.Y), Mm.FromDiu(b.Width), Mm.FromDiu(b.Height));
    }

    private static (double X, double Y, double Width, double Height) MeasuredInk(LabelTemplate template)
    {
        var layout = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1, "判据.xlsx"));
        var item = layout.Items.OfType<TextItem>().Single();
        return TextInkBox.Measure(item)!.Value;
    }

    // ---------- ① 墨迹框要落在字真正印出来的地方 ----------

    [Fact]
    public void TheFrameOfACenteredLineSitsWhereTheGlyphsPrint() => OnStaThread(() =>
    {
        var template = Paper(UsersCenteredLine());
        var printed = PrintedInk(template);
        var frame = MeasuredInk(template);

        Assert.True(Math.Abs(printed.X - frame.X) < 1.5,
            $"框的左缘 {frame.X:0.#} mm，字实际印在 {printed.X:0.#} mm——差 {Math.Abs(printed.X - frame.X):0.#} mm");
        Assert.True(Math.Abs(printed.Y - frame.Y) < 1.5,
            $"框的上缘 {frame.Y:0.#} mm，字实际印在 {printed.Y:0.#} mm");
        Assert.True(Math.Abs(printed.Right - frame.X - frame.Width) < 1.5,
            $"框的右缘 {frame.X + frame.Width:0.#} mm，字实际印到 {printed.Right:0.#} mm");
        Assert.True(Math.Abs(printed.Bottom - frame.Y - frame.Height) < 1.5,
            $"框的下缘 {frame.Y + frame.Height:0.#} mm，字实际印到 {printed.Bottom:0.#} mm");
    });

    /// <summary>夹具自检：这一行的行盒比字形大得多，所以"框=行盒"那种写法一定会被上面那条抓到。</summary>
    [Fact]
    public void TheLineBoxIsMuchBiggerThanTheGlyphsSoTheOldMeasureCouldNotPass() => OnStaThread(() =>
    {
        var template = Paper(UsersCenteredLine());
        var printed = PrintedInk(template);
        var item = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1, "判据.xlsx"))
            .Items.OfType<TextItem>().Single();
        var fit = TextFit.Solve(item, 1.0, TextFit.CanonicalPixelsPerDip)!;
        var lineBoxHeightMm = Mm.FromDiu(fit.Formatted.Height) * item.TextScaleY;

        Assert.True(lineBoxHeightMm > printed.Height * 1.3,
            $"行盒高 {lineBoxHeightMm:0.#} mm 只比字形 {printed.Height:0.#} mm 大一点，这条夹具测不到东西");
    });

    [Fact]
    public void ALeftAlignedLineInAWideBandStillFramesItsOwnText() => OnStaThread(() =>
    {
        var template = Paper(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "MADE IN CHINA", X = 5, Y = 20, Width = 130, Height = 12,
            FontSizePt = 14, Align = HorizontalAlign.Left, WrapWidthMm = 130,
        });
        var printed = PrintedInk(template);
        var frame = MeasuredInk(template);
        Assert.True(Math.Abs(printed.X - frame.X) < 1.5 && Math.Abs(printed.Bottom - frame.Y - frame.Height) < 1.5,
            $"左对齐也一样：框 ({frame.X:0.#},{frame.Y:0.#}) 尺寸 {frame.Width:0.#}×{frame.Height:0.#}，" +
            $"印出来 ({printed.X:0.#},{printed.Y:0.#}) 尺寸 {printed.Width:0.#}×{printed.Height:0.#}");
    });

    // ---------- ② 文本允许探出纸外，且不再因此存不了盘 ----------

    [Fact]
    public void ATextLineCanBeDraggedSoItsInkHangsOffThePaperAndItStillSaves() => OnStaThread(() =>
    {
        var template = Paper(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "olu830-58", X = 5, Y = 40, Width = 60, Height = 14,
            FontSizePt = 20, Align = HorizontalAlign.Center, WrapWidthMm = 60,
        });
        var vm = NewVm("drag-off", template);
        vm.SnapEnabled = false;
        var text = vm.Template.Elements[0];
        var ink = vm.DisplayBoxOf(text)!.Value;

        vm.BeginDrag(ink.X + ink.Width / 2, ink.Y + ink.Height / 2, 0.3);
        vm.DragTo(ink.X + ink.Width / 2 + 120, ink.Y + ink.Height / 2 + 90);   // 拖到必然越过纸边，否则这条测不到钳制
        vm.EndDrag();

        var after = vm.DisplayBoxOf(text)!.Value;
        Assert.True(after.X + after.Width > template.WidthMm + 1,
            $"墨迹右缘只到 {after.X + after.Width:0.#}，纸宽 {template.WidthMm}——还是被钉在纸内");
        Assert.True(after.Y + after.Height > template.HeightMm + 1, "下缘同理，要能探出纸外");
        Assert.False(vm.HasError, $"探出纸外只能提醒，不能拦存盘：{string.Join(" || ", vm.Issues)}");
        Assert.True(vm.Issues.Any(m => m.Contains("探出", StringComparison.Ordinal)),
            "提醒还得看得见（清单里要有那一条），不能默默放过去");
    });

    [Fact]
    public void ARectangleStillStopsAtThePaperEdge() => OnStaThread(() =>
    {
        var template = Paper(new TemplateElement { Kind = ElementKind.Rect, X = 5, Y = 5, Width = 20, Height = 10 });
        var vm = NewVm("rect-edge", template);
        vm.SnapEnabled = false;
        var rect = vm.Template.Elements[0];

        vm.BeginDrag(15, 10, 0.3);
        vm.DragTo(215, 110);
        vm.EndDrag();

        Assert.Equal(template.WidthMm, rect.X + rect.Width, 3);     // 框就是会印的东西，贴住纸边就该停
        Assert.Equal(template.HeightMm, rect.Y + rect.Height, 3);
    });

    // ---------- ③ 元素框可以关掉 ----------

    [Fact]
    public void TurningOffElementBoxesLeavesOnlyTheSelectedFrame() => OnStaThread(() =>
    {
        var template = Paper(
            new TemplateElement { Kind = ElementKind.Rect, X = 5, Y = 5, Width = 20, Height = 10 },
            new TemplateElement { Kind = ElementKind.Rect, X = 40, Y = 30, Width = 20, Height = 10 },
            new TemplateElement { Kind = ElementKind.Rect, X = 70, Y = 60, Width = 20, Height = 10 });
        var vm = NewVm("boxes", template);
        vm.SelectedRow = vm.Elements[1];
        var window = new TemplateEditorWindow(vm);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1180, 760));
        root.Arrange(new Rect(0, 0, 1180, 760));
        window.UpdateLayout();

        var on = CountRectsDrawnWith(FrameOf(window.EditorCanvas), UnselectedBoxColor);
        Assert.Equal(2, on);                                   // 开着：三只里未选中的那两只各画一条（选中的用另一支笔）

        vm.ShowElementBoxes = false;
        window.UpdateLayout();
        var off = CountRectsDrawnWith(FrameOf(window.EditorCanvas), UnselectedBoxColor);
        Assert.Equal(0, off);                                  // 关掉：未选中的不画
        Assert.True(CountRectsDrawnWith(FrameOf(window.EditorCanvas), SelectedBoxColor) >= 1,
            "选中的那只要一直画着——不然看不见在改谁，句柄也没了");
    });

    /// <summary>收一帧画布的真实绘制（<c>UIElement.RenderOpen</c> 是 protected，测试只能从控件给的钩子拿）。</summary>
    private static DrawingGroup FrameOf(TemplateEditorControl canvas)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
            canvas.RenderForTests(dc);
        return group;
    }

    /// <summary>未选中的框那支笔（半透明蓝）与选中的那支（实心蓝）——颜色对不上就是没画。</summary>
    private static readonly Color UnselectedBoxColor = Color.FromArgb(150, 60, 130, 200);
    private static readonly Color SelectedBoxColor = Color.FromRgb(0, 120, 215);

    private static int CountRectsDrawnWith(Drawing? drawing, Color penColor)
    {
        switch (drawing)
        {
            case null: return 0;
            case DrawingGroup group: return group.Children.Sum(child => CountRectsDrawnWith(child, penColor));
            case GeometryDrawing geometry when geometry.Pen?.Brush is SolidColorBrush brush
                                               && brush.Color == penColor: return 1;
            default: return 0;
        }
    }

    // ---------- ④ 画布上按下那一点就是起点 ----------

    [Fact]
    public void AShapeDrawnOverFullWidthBandsStartsWhereTheMouseWentDown() => OnStaThread(() =>
    {
        // 用户那份模板的形状：每一行都是通栏行带 → 满纸"重叠"，从前每一下都被搬到别处
        var template = Paper(
            new TemplateElement { Kind = ElementKind.Text, Text = "第一行", X = 5, Y = 5, Width = 130, Height = 30 },
            new TemplateElement { Kind = ElementKind.Text, Text = "第二行", X = 5, Y = 37, Width = 130, Height = 18 },
            new TemplateElement { Kind = ElementKind.Text, Text = "第三行", X = 5, Y = 57, Width = 130, Height = 18 });
        var vm = NewVm("shape-start", template);

        Assert.True(vm.BeginShape(TemplateEditorViewModel.EditorTool.Rect, 47.3, 61.7));
        var drawn = vm.Template.Elements[^1];
        Assert.Equal(47.3, drawn.X, 3);
        Assert.Equal(61.7, drawn.Y, 3);

        vm.DragShape(77.3, 81.7);
        vm.EndShape();
        Assert.Equal(30, drawn.Width, 2);
        Assert.Equal(20, drawn.Height, 2);
    });

    [Fact]
    public void TheFirstPointOfACurveIsTheClick() => OnStaThread(() =>
    {
        var template = Paper(new TemplateElement { Kind = ElementKind.Text, Text = "通栏行带", X = 5, Y = 5, Width = 130, Height = 90 });
        var vm = NewVm("path-start", template);

        Assert.True(vm.BeginPath(33.7, 44.9));
        var drawn = vm.Template.Elements[^1];
        Assert.Equal(33.7, drawn.X, 3);
        Assert.Equal(44.9, drawn.Y, 3);
    });

    /// <summary>工具栏那颗「添加文本」仍要找空位——那里的位置是软件猜的，该躲开别人。</summary>
    [Fact]
    public void ATextAddedFromTheToolbarStillLooksForAFreeSpot() => OnStaThread(() =>
    {
        var template = Paper(new TemplateElement { Kind = ElementKind.Text, Text = "占满整张", X = 0, Y = 0, Width = 140, Height = 100 });
        var vm = NewVm("toolbar-add", template);
        vm.SelectedRow = null;

        vm.AddTextCommand.Execute(null);
        var added = vm.Template.Elements[^1];
        Assert.NotSame(template.Elements[0], added);
        Assert.True(vm.Template.Elements.Count == 2);
    });

    // ---------- ⑤ 粘贴（别的软件复制的图片、文字） ----------

    /// <summary>
    /// 把内容放进系统剪贴板，并<strong>读回来确认</strong>。
    /// <para>为什么不信 <c>Set</c> 方法：本机随时可能有别的进程（剪贴板历史、远程桌面、输入法）一时占着
    /// <c>OpenClipboard</c>，而 WPF 的 <c>SetText</c> 是在<strong>内容已经设进去之后</strong>的 <c>Flush()</c> 才抛
    /// <c>CLIPBRD_E_CANT_OPEN</c>——报错不等于没放进去。判据要的是"剪贴板里真有这一份"，那就直接验这一件事。</para>
    /// <para>文字那条走 <c>SetDataObject(copy:false)</c>：它压根不 Flush，也就没那一步可抛。</para>
    /// </summary>
    private static void PutOnClipboard(Action set, Func<bool> isThere)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { set(); } catch (Exception) { /* 交给下面的回读判定 */ }
            try { if (isThere()) return; } catch (Exception) { /* 读也一样：可能被占着 */ }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException("剪贴板一直被别的进程占着，这一条测不了粘贴（本机环境问题，不是产品缺陷）");
    }

    [Fact]
    public void PastingTextFromAnotherProgramAddsATextElement() => OnStaThread(() =>
    {
        const string copied = "GIRAR RETROEXCAVADORA\n90/13B";
        var vm = NewVm("paste-text", Paper());
        PutOnClipboard(
            () => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, copied), copy: false),
            () => Clipboard.ContainsText() && Clipboard.GetText() == copied);

        Assert.True(vm.PasteCommand.CanExecute(null));
        vm.PasteCommand.Execute(null);

        var pasted = Assert.Single(vm.Template.Elements);
        Assert.Equal(ElementKind.Text, pasted.Kind);
        Assert.Equal(copied, pasted.Text);
    });

    [Fact]
    public void PastingAnImageCopiesItIntoTheTemplateFolderAndAddsAnImageElement() => OnStaThread(() =>
    {
        var (vm, folder) = NewEditorVm("paste-img", Paper());
        var image = RedSquare(20, 12);
        PutOnClipboard(() => Clipboard.SetImage(image), () => Clipboard.ContainsImage());

        vm.PasteCommand.Execute(null);

        var pasted = Assert.Single(vm.Template.Elements);
        Assert.Equal(ElementKind.Image, pasted.Kind);
        var file = Path.Combine(folder, pasted.ImagePath!);
        Assert.True(File.Exists(file), $"粘贴的图片没落到模板目录：{pasted.ImagePath}");
        // 尺寸口径：按图片自己的 DPI 换成毫米＝"原样大"，不凭空放大（放大只会糊）——20×12px @96dpi ≈ 5.3×3.2mm
        Assert.Equal(5.29, pasted.Width, 1);
        Assert.Equal(3.18, pasted.Height, 1);
    });

    [Fact]
    public void PastingNothingSaysSoInsteadOfGoingQuiet() => OnStaThread(() =>
    {
        var vm = NewVm("paste-empty", Paper(
            new TemplateElement { Kind = ElementKind.Rect, X = 5, Y = 5, Width = 10, Height = 10 }));
        PutOnClipboard(Clipboard.Clear, () => !Clipboard.ContainsImage() && !Clipboard.ContainsText());

        vm.PasteCommand.Execute(null);

        Assert.Single(vm.Template.Elements);                                  // 什么都没加
        Assert.Contains("剪贴板", vm.StatusText);                              // 但要说清楚
    });

    private static BitmapSource RedSquare(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 30; pixels[i + 2] = 30; pixels[i + 3] = 255; }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---------- ⑥ 新建模板不再自带一条删不掉的黑框 ----------

    [Fact]
    public void ANewTemplateCarriesNoBorderThatCannotBeDeleted()
    {
        var template = TemplateFactory.Blank("我的唛头模板");

        Assert.Equal(0, template.BorderMm);       // 那条不是元素、图层列表里没有它 → 删不掉却会印
        Assert.Contains(template.Elements, e => e.Kind == ElementKind.Rect);   // 可删的那只框留着
        Assert.DoesNotContain(TemplateValidator.Validate(template),
            i => i.Severity == IssueLevel.Error);
    }

    /// <summary>他手上那份 `我的唛头模板.json` 就是这个形状：元素全删了，外框还在。</summary>
    [Fact]
    public void ABorderOnlyTemplateStillSaysThePageWouldPrintAFrame()
    {
        var template = new LabelTemplate { Id = "user.left", Name = "只剩外框", WidthMm = 100, HeightMm = 80, BorderMm = 0.5 };
        var issues = TemplateValidator.Validate(template);
        Assert.Contains(issues, i => i.Message.Contains("外框", StringComparison.Ordinal));
    }
}
