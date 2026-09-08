using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Editing;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 模板编辑器的操作链路：鼠标按下 → 拖动（含吸附与贴边）→ 松手 → 属性面板 → 保存入库。
/// <para>
/// 只测 VM（几何判断在 Core，控件只搬像素），但一定要走真实命令与真实模板库目录，
/// 因为"编辑器改完主窗口能不能选到"这件事恰恰坏在存储与事件衔接上。
/// </para>
/// </summary>
public class TemplateEditorFlowTests
{
    private static void WithTempFolder(Action<string> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "labelgou-m4-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            body(folder);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不影响结论
            }
        }
    }

    private static void OnStaThread(Action work)
        => LabelGou.App.Export.StaWorker
            .RunAsync<bool>((_, _) => { work(); return true; }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static LabelTemplate Working(string name = "拖改测试") => new()
    {
        Id = "user.flow",
        Name = name,
        WidthMm = 100,
        HeightMm = 80,
        PaddingMm = 4,
        BuiltIn = false,
        Elements =
        {
            new TemplateElement { Kind = ElementKind.Text, X = 10, Y = 10, Width = 20, Height = 6, Text = "顶部标题" },
            new TemplateElement { Kind = ElementKind.Rect, X = 10, Y = 30, Width = 30, Height = 12 },
        },
    };

    private static TemplateEditorViewModel NewVm(string folder, LabelTemplate? working = null)
        => new(working ?? Working(), new TemplateStore(folder));

    /// <summary>元素中心（毫米）：第一个元素 (10,10,20,6) 的中心。</summary>
    private const double FirstCenterX = 20;
    private const double FirstCenterY = 13;

    [Fact]
    public void DraggingMovesTheElementAndMarksDirty() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SnapEnabled = false;
        var canvasRepaints = 0;
        vm.CanvasChanged += () => canvasRepaints++;

        var mode = vm.BeginDrag(FirstCenterX, FirstCenterY, 0.3);
        Assert.Equal(TemplateEditorViewModel.DragMode.Move, mode);
        vm.DragTo(FirstCenterX + 7, FirstCenterY + 4);
        vm.EndDrag();

        var element = vm.Template.Elements[0];
        Assert.Equal(17, element.X, 6);
        Assert.Equal(14, element.Y, 6);
        Assert.True(vm.IsDirty);
        Assert.True(canvasRepaints > 0, "拖动过程中必须通知画布重画");
    });

    [Fact]
    public void DragSnapsToTheCenterLineAndShowsGuide() => OnStaThread(() =>
    {
        var single = Working();
        single.Elements.RemoveAt(1);   // 只留一个元素，确保贴到的是标签中心线而不是邻居边
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], single);
        vm.SnapEnabled = true;
        vm.SnapToGrid = false;

        vm.BeginDrag(FirstCenterX, FirstCenterY, 0.3);
        vm.DragTo(49.6, FirstCenterY);   // 左边 39.6，元素中心 49.6 → 差 0.4mm 就该吸到 50

        var element = vm.Template.Elements[0];
        Assert.Equal(40, element.X, 6);
        Assert.Equal(GuideSource.LabelCenter, Assert.Single(vm.ActiveGuides).Source);
        vm.EndDrag();
        Assert.Empty(vm.ActiveGuides);
    });

    [Fact]
    public void DragPastTheBorderStopsThere() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SnapEnabled = false;

        vm.BeginDrag(FirstCenterX, FirstCenterY, 0.3);
        vm.DragTo(500, 500);
        vm.EndDrag();

        var element = vm.Template.Elements[0];
        Assert.Equal(80, element.X, 6);
        Assert.Equal(74, element.Y, 6);
    });

    [Fact]
    public void CancelDragPutsTheElementBack() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SnapEnabled = false;

        vm.BeginDrag(FirstCenterX, FirstCenterY, 0.3);
        vm.DragTo(60, 60);
        vm.CancelDrag();

        Assert.Equal(10, vm.Template.Elements[0].X, 6);
        Assert.Equal(10, vm.Template.Elements[0].Y, 6);
    });

    [Fact]
    public void UndoAfterDragBringsTheOldPositionBack() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SnapEnabled = false;

        vm.BeginDrag(FirstCenterX, FirstCenterY, 0.3);
        vm.DragTo(40, 30);
        vm.EndDrag();
        vm.Undo();

        Assert.Equal(10, vm.Template.Elements[0].X, 6);
        vm.Redo();
        Assert.Equal(30, vm.Template.Elements[0].X, 6);
    });

    [Fact]
    public void PropertyPanelWritesStraightIntoTheTemplate() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SelectedRow = vm.Elements[0];

        Assert.NotNull(vm.Editing);
        vm.Editing!.X = 33;
        vm.Editing.Text = "{{Consignee}}";

        Assert.Equal(33, vm.Template.Elements[0].X, 6);
        Assert.Equal("{{Consignee}}", vm.Template.Elements[0].Text);
        Assert.True(vm.IsDirty);

        vm.Undo();
        Assert.NotEqual("{{Consignee}}", vm.Template.Elements[0].Text);
    });

    [Fact]
    public void AddAndRemoveKeepTheListAndSelectionInStep() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);

        vm.AddTextCommand.Execute(null);
        Assert.Equal(3, vm.Template.Elements.Count);
        Assert.Equal(3, vm.Elements.Count);
        Assert.NotNull(vm.SelectedRow);
        Assert.StartsWith("03", vm.Elements[^1].Display);
        Assert.Contains("文本", vm.Elements[^1].Display);

        vm.RemoveCommand.Execute(null);
        Assert.Equal(2, vm.Template.Elements.Count);
        Assert.Equal(2, vm.Elements.Count);
        Assert.NotNull(vm.SelectedRow);
    });

    [Fact]
    public void ReorderingDoesNotLoseTheSelectedElement() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SelectedRow = vm.Elements[0];
        var picked = vm.SelectedRow!.Element;

        vm.BringToFrontCommand.Execute(null);

        Assert.Same(picked, vm.SelectedRow!.Element);
        Assert.Same(picked, vm.Template.Elements[^1]);
        vm.SendToBackCommand.Execute(null);
        Assert.Same(picked, vm.Template.Elements[0]);
    });

    [Fact]
    public void AlignCommandsMoveAgainstTheLabel() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SelectedRow = vm.Elements[0];
        var yBefore = vm.Template.Elements[0].Y;

        // 单轴：点「水平居中」只该动 X。上一版把 Y 也甩到 0（想居中就得重贴一次顶）
        vm.AlignCommand.Execute("Center");
        Assert.Equal(40, vm.Template.Elements[0].X, 6);
        Assert.Equal(yBefore, vm.Template.Elements[0].Y, 6);

        vm.AlignCommand.Execute("Top");
        Assert.Equal(40, vm.Template.Elements[0].X, 6);      // 顶对齐也不能反过来改水平位置
        Assert.Equal(0, vm.Template.Elements[0].Y, 6);

        vm.PaddingCommand.Execute("Bottom");
        Assert.Equal(4, vm.Template.Elements[0].X, 6);
        Assert.Equal(70, vm.Template.Elements[0].Y, 6);
    });

    [Fact]
    public void InsertFieldAppendsToSelectedText() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SelectedRow = vm.Elements[0];

        vm.InsertFieldCommand.Execute("GrossWeight");

        Assert.Equal("顶部标题{{GrossWeight}}", vm.Template.Elements[0].Text);
        Assert.False(vm.HasError);
    });

    [Fact]
    public void NudgeMovesByTheGivenMillimetres() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SelectedRow = vm.Elements[0];

        vm.Nudge(0.1, -0.1);

        Assert.Equal(10.1, vm.Template.Elements[0].X, 6);
        Assert.Equal(9.9, vm.Template.Elements[0].Y, 6);
    });

    [Fact]
    public void SaveWritesIntoTheTemplateLibrary() => WithTempFolder(folder => OnStaThread(() =>
    {
        var vm = NewVm(folder);
        LabelTemplate? savedTemplate = null;
        vm.Saved += t => savedTemplate = t;
        var closed = false;
        vm.CloseRequested += () => closed = true;

        vm.SelectedRow = vm.Elements[0];
        vm.Editing!.X = 45;
        vm.Save();

        Assert.NotNull(savedTemplate);
        Assert.True(closed, "存好就该关窗口，别让人再确认一次");
        Assert.False(vm.IsDirty);
        var listed = new TemplateStore(folder).ListAll().Single(t => !t.BuiltIn);
        Assert.Equal(45, listed.Elements[0].X, 6);
    }));

    [Fact]
    public void RenamingAndSavingLeavesOnlyOneFile() => WithTempFolder(folder => OnStaThread(() =>
    {
        var vm = NewVm(folder, Working("旧名字"));
        vm.Save();
        Assert.Single(Directory.GetFiles(folder, "*.json"));

        vm.Name = "新名字";
        vm.Save();

        var files = Directory.GetFiles(folder, "*.json");
        Assert.Single(files);
        Assert.Contains("新名字", Path.GetFileName(files[0]));
    }));

    [Fact]
    public void SaveIsRefusedWhenTheTemplateWouldNotPrint() => WithTempFolder(folder => OnStaThread(() =>
    {
        var vm = NewVm(folder);
        var errors = new List<string>();
        vm.ErrorRaised += message => errors.Add(message);

        vm.SelectedRow = vm.Elements[0];
        vm.Editing!.Text = "{{这个字段不存在}}";

        Assert.True(vm.HasError);
        vm.Save();

        Assert.NotEmpty(errors);
        Assert.Empty(Directory.GetFiles(folder, "*.json"));
        Assert.True(vm.IsDirty);
    }));

    [Fact]
    public void ShrinkingTheLabelKeepsEveryElementOnThePaper() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);

        vm.WidthMm = 40;
        vm.HeightMm = 30;

        foreach (var element in vm.Template.Elements)
        {
            var box = EditGeometry.BoxOf(element);
            Assert.True(box.X + box.Width <= 40 + TemplateValidator.ToleranceMm, $"{element.Kind} 右侧跑出去了");
            Assert.True(box.Y + box.Height <= 30 + TemplateValidator.ToleranceMm, $"{element.Kind} 下方跑出去了");
        }
        Assert.False(vm.HasError);
    });

    // ---------- 画布与窗口真的能加载 ----------

    [Fact]
    public void CanvasPaintsTheLabelAtTheRightSize() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], TemplateFactory.Blank("我的唛头"));
        vm.SelectedRow = vm.Elements[1];
        vm.GridStepMm = 10;   // 步长定成 10mm，才能拿像素断言“网格真画在纸上”
        var control = new TemplateEditorControl { DataContext = vm };

        control.Measure(new Size(700, 500));
        control.Arrange(new Rect(0, 0, 700, 500));
        control.UpdateLayout();

        Assert.True(control.CurrentZoom > 0.05, "自适应缩放没算出来");

        var bitmap = new RenderTargetBitmap(700, 500, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(control);
        var source = BitmapSourceCreate(bitmap);

        Point mm(double xMm, double yMm) => new(
            (int)(control.CurrentOrigin.X + Mm.ToDiu(xMm) * control.CurrentZoom),
            (int)(control.CurrentOrigin.Y + Mm.ToDiu(yMm) * control.CurrentZoom));

        // 纸外是灰底；纸上不是网格线的地方是白纸；正好在网格线上的点要被网格加深
        var outside = ReadPixel(source, 3, 3);
        Assert.True(outside.Item1 < 250, $"纸外应当是灰底，实际 RGB{outside}");
        var blank = ReadPixel(source, (int)mm(55.5, 45.5).X, (int)mm(55.5, 45.5).Y);
        Assert.True(blank.Item1 > 248, $"网格之间应当是白纸，实际 RGB{blank}");
        var onGrid = ReadPixel(source, (int)mm(50, 45.5).X, (int)mm(50, 45.5).Y);
        Assert.True(onGrid.Item1 < blank.Item1 - 8,
            $"网格线应当看得见（白 {blank} vs 线上 {onGrid}）——白底盖住网格就是本条在报");

        // 样例数据要真的排上去（顶部客户名那一行里应有深色字）
        var ink = DarkestInRow(source, (int)mm(6, 10).Y, (int)mm(6, 10).X, (int)mm(94, 10).X);
        Assert.True(ink < 120, $"顶部应当画出样例客户名，最暗像素 {ink}");

        // 设了环境变量就留下真产物，给人用眼睛复核“编辑器看起来对不对”
        var artifacts = Environment.GetEnvironmentVariable("LABELGOU_TEST_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(artifacts))
        {
            Directory.CreateDirectory(artifacts);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(artifacts, "编辑器画布.png"));
            encoder.Save(stream);
        }
    });

    [Fact]
    public void EditorWindowXamlLoadsAndBinds() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        var window = new TemplateEditorWindow(vm);

        Assert.NotNull(window.Content);
        Assert.Same(vm, window.DataContext);

        // 没 Show 的 Window 尺寸由 HWND 驱动，量不出来；直接量它的内容根就能验证整棵树布好了
        var root = (FrameworkElement)window.Content!;
        root.Measure(new Size(1180, 760));
        root.Arrange(new Rect(0, 0, 1180, 760));
        root.UpdateLayout();

        Assert.NotNull(window.EditorCanvas);
        Assert.Same(vm, window.EditorCanvas.DataContext);
        Assert.True(window.EditorCanvas.RenderSize.Width > 50, "画布没拿到尺寸");
        Assert.True(window.EditorCanvas.RenderSize.Height > 50, "画布没拿到尺寸");
        window.AllowCloseWithoutPrompt = true;
        window.Close();
    });

    private static BitmapSource BitmapSourceCreate(BitmapSource bitmap)
    {
        bitmap.Freeze();
        return bitmap;
    }

    private static (byte, byte, byte) ReadPixel(BitmapSource source, int x, int y)
    {
        x = Math.Clamp(x, 0, source.PixelWidth - 1);
        y = Math.Clamp(y, 0, source.PixelHeight - 1);
        var buffer = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return (buffer[2], buffer[1], buffer[0]);
    }

    /// <summary>一行像素里最暗的通道值（拿来看“这里有没有墨”）。</summary>
    private static int DarkestInRow(BitmapSource source, int y, int fromX, int toX)
    {
        var width = Math.Max(1, toX - fromX);
        var buffer = new byte[width * 4];
        source.CopyPixels(new Int32Rect(fromX, Math.Clamp(y, 0, source.PixelHeight - 1), width, 1), buffer, width * 4, 0);
        var darkest = 255;
        for (var i = 0; i + 2 < buffer.Length; i += 4)
        {
            darkest = Math.Min(darkest, Math.Min(buffer[i], Math.Min(buffer[i + 1], buffer[i + 2])));
        }
        return darkest;
    }
}
