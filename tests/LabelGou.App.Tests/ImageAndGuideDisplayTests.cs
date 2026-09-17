using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Impos;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 82 棒：图片进模板的框要等于图的形状（①）、编辑器那两条常驻中心虚线（②）、
/// 整版拼版预览里的单标签分界线（③，只在屏幕上，五条出口都拿不到它）。
/// <para>②③ 都判"这一帧到底画没画"——用画出来的DrawingTree 说话，不读属性：
/// 第 50 棒那条教训（只写在控件里的规则，VM 层测试全绿也照不出来）还挂在 §五-151。</para>
/// </summary>
public class ImageAndGuideDisplayTests
{
    private static void OnStaThread(Action work)
        => LabelGou.App.Export.StaWorker
            .RunAsync<bool>((_, _) => { work(); return true; }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static (TemplateEditorViewModel Vm, string Folder) NewEditor(string tag, LabelTemplate working)
    {
        var folder = Path.Combine(Path.GetTempPath(), "labelgou-b82-" + tag + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return (new TemplateEditorViewModel(working, new TemplateStore(folder)), folder);
    }

    private static LabelTemplate Paper(double w = 140, double h = 100) => new()
    {
        Id = "user.b82", Name = "第 82 棒", WidthMm = w, HeightMm = h, PaddingMm = 5, BorderMm = 0,
    };

    /// <summary>写一张真实 PNG（像素与 DPI 都由测试定），返回路径。</summary>
    private static string WritePng(string dir, int px, int py, double dpi = 96)
    {
        var pixels = new byte[px * py * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 90; pixels[i + 2] = 160; pixels[i + 3] = 255; }
        var bmp = BitmapSource.Create(px, py, dpi, dpi, PixelFormats.Bgra32, null, pixels, px * 4);
        bmp.Freeze();
        var path = Path.Combine(dir, $"sample-{px}x{py}.png");
        using var stream = File.Create(path);
        new PngBitmapEncoder { Frames = { BitmapFrame.Create(bmp) } }.Save(stream);
        return path;
    }

    // ---------- ① 导入图片：框要等于图的形状 ----------

    [Fact]
    public void AnImportedImageGetsABoxWithThePicturesOwnAspectRatio() => OnStaThread(() =>
    {
        var (vm, folder) = NewEditor("img", Paper());
        var file = WritePng(folder, 800, 600);

        vm.AddImageFile(file);

        var added = Assert.Single(vm.Template.Elements);
        Assert.Equal(ElementKind.Image, added.Kind);
        Assert.Equal(800 / 600d, added.Width / added.Height, 3);
        Assert.False(Math.Abs(added.Width - 20) < 0.01 && Math.Abs(added.Height - 12) < 0.01,
            "还是那个写死的 20×12——图进来就被压扁");
        Assert.True(added.Width <= 140 - 2 * 5 + 0.01, $"原样大装不下时该等比缩到内容区：{added.Width:0.#}mm");
        Assert.True(File.Exists(Path.Combine(folder, added.ImagePath!)), "图片没复制进模板目录，换机器就只剩空框");
    });

    [Fact]
    public void ASmallImportKeepsItsNaturalSizeInsteadOfBeingBlownUp() => OnStaThread(() =>
    {
        var (vm, folder) = NewEditor("img-small", Paper());
        var file = WritePng(folder, 60, 20);

        vm.AddImageFile(file);

        var added = Assert.Single(vm.Template.Elements);
        Assert.Equal(15.88, added.Width, 1);     // 60px @96dpi = 15.88mm
        Assert.Equal(5.29, added.Height, 1);
    });

    // ---------- ② 编辑器：横竖中心虚线 ----------

    [Fact]
    public void TheEditorDrawsTwoStandingCenterLinesAcrossThePaper() => OnStaThread(() =>
    {
        var (vm, _) = NewEditor("center", Paper());
        var window = new TemplateEditorWindow(vm);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1180, 760));
        root.Arrange(new Rect(0, 0, 1180, 760));
        window.UpdateLayout();
        var canvas = window.EditorCanvas;

        var lines = CenterLinesOf(canvas);
        Assert.Equal(2, lines.Count);
        var vertical = lines.Single(b => b.Width < 1);
        var horizontal = lines.Single(b => b.Height < 1);

        // 位置：纸的正中（不是内容区正中，也不是画布正中）。比的是外接框的**中心**——
        // Drawing.Bounds 含笔宽（实测向两侧各外扩半个笔宽 0.4），拿 X/Y 比就差那半笔。
        var origin = canvas.CurrentOrigin;
        var zoom = canvas.CurrentZoom;
        Assert.Equal(origin.X + Mm.ToDiu(70) * zoom, vertical.X + vertical.Width / 2, 1);
        Assert.Equal(origin.Y + Mm.ToDiu(50) * zoom, horizontal.Y + horizontal.Height / 2, 1);
        // 像 CorelDRAW 那样伸出纸外，才看得出"这条线是整张纸的中线"
        Assert.True(horizontal.Width > Mm.ToDiu(140) * zoom, "横线没伸出纸外，那就是内边距线不是中心线");

        vm.ShowCenterLines = false;
        window.UpdateLayout();
        Assert.Empty(CenterLinesOf(window.EditorCanvas));
    });

    private static List<Rect> CenterLinesOf(TemplateEditorControl canvas)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
            canvas.RenderForTests(dc);
        var found = new List<Rect>();
        Collect(group, found);
        return found;

        void Collect(Drawing? drawing, List<Rect> sink)
        {
            switch (drawing)
            {
                case null: return;
                case DrawingGroup g: foreach (var child in g.Children) Collect(child, sink); break;
                case GeometryDrawing geometry when ReferenceEquals(geometry.Pen, TemplateEditorControl.CenterPen):
                    sink.Add(geometry.Bounds); break;
            }
        }
    }

    // ---------- ③ 整版拼版：单标签分界线，只给屏幕 ----------

    /// <summary>一页两枚的整版（297×210 上排 140×100，页边 5、缝 2）。Build 的第四个参数是**这批有几枚**。</summary>
    private static SheetPlan MultiUpPlan() => ImpositionEngine.Build(new SheetSpec
    {
        Id = "test.b82", Name = "一开四",
        PaperWidthMm = 297, PaperHeightMm = 210,
        MarginLeftMm = 5, MarginTopMm = 5, MarginRightMm = 5, MarginBottomMm = 5,
        GutterXMm = 2, GutterYMm = 2, CropMarkThicknessMm = 0.15,
        RepeatSameLabelPerPage = false,
    }, 140, 100, 44);

    private static int CountDividers(PageRenderPurpose purpose, bool showDividers, bool includeTrimMarks)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
            SheetRenderer.DrawPage(dc, MultiUpPlan(), 1, 1.0,
                layoutProvider: null, showElementGuides: false, purpose, 1.0, includeTrimMarks,
                showLabelDividers: showDividers);
        var n = 0;
        Collect(group);
        return n;

        void Collect(Drawing? drawing)
        {
            switch (drawing)
            {
                case null: return;
                case DrawingGroup g: foreach (var child in g.Children) Collect(child); break;
                case GeometryDrawing geometry when ReferenceEquals(geometry.Pen, SheetRenderer.LabelDividerPen): n++; break;
            }
        }
    }

    [Fact]
    public void TheImpositionPreviewOutlinesEveryLabelOnScreenOnly()
    {
        var perPage = MultiUpPlan().PlacementsOnPage(1).Count;
        Assert.True(perPage >= 2, $"夹具得真是一页多枚，否则这条测不到分界：{perPage}");
        Assert.Equal(perPage, CountDividers(PageRenderPurpose.Screen, showDividers: true, includeTrimMarks: false));
        Assert.Equal(0, CountDividers(PageRenderPurpose.Screen, showDividers: false, includeTrimMarks: false));
        // 位图与打印两个用途拿不到它——"仅预览使用不会被打印"这句要靠闸门，不靠自觉
        Assert.Equal(0, CountDividers(PageRenderPurpose.Image, showDividers: true, includeTrimMarks: true));
        Assert.Equal(0, CountDividers(PageRenderPurpose.Printer, showDividers: true, includeTrimMarks: true));
    }

    [Fact]
    public void TheDividerDoesNotNeedCropMarksOrADieLineInThePaperSpec()
    {
        // 关掉「含裁切线」（includeTrimMarks=false）也照样有分界：这两件事不是一回事
        Assert.Equal(MultiUpPlan().PlacementsOnPage(1).Count,
            CountDividers(PageRenderPurpose.Screen, showDividers: true, includeTrimMarks: false));
        // 而纸规那格 LabelOutlineMm 仍是 0（没让他去填刀模数）
        Assert.Equal(0, MultiUpPlan().Spec.LabelOutlineMm);
    }
}
