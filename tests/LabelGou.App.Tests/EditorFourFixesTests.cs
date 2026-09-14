using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core.Barcodes;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 59 棒：用户 2026-09-14 一次提的四条（附两张截图）。
/// <para>①「在编辑栏对条码拖动右下角进行等比放大时只对条码进行拉长，并没有等比放大」——
/// X 尺寸来自参数、条高来自框，所以拖角只把条拉高；②「将形状工具调整到左列表第一个」；
/// ③「条码在外栏设置好时内部显示表格中第一个条码而不是（图二错误）」——画布拿<strong>列名</strong>去编
/// EAN-13，只剩那句红字；④「图层：编辑面板显示在上面的，外面编辑栏显示在下面 ✗」——
/// 列表顶部那条其实是最底层，照 CorelDRAW 的对象管理器改成最上＝压在上面。</para>
/// <para>四条都是"编译与绑定全都看不见、只有量一次布局或走一次手势才看得见"的那类，所以都做成量具。</para>
/// </summary>
public class EditorFourFixesTests
{
    private static void OnStaThread(Action work)
        => StaWorker.RunAsync((_, _) =>
        {
            work();
            return true;
        }, null, CancellationToken.None).GetAwaiter().GetResult();

    private static string TempFolder(string tag)
        => Path.Combine(Path.GetTempPath(), "labelgou-b59-" + tag + "-" + Guid.NewGuid().ToString("N")[..6]);

    private static TemplateEditorViewModel NewVm(string folder, LabelTemplate? working = null)
        => new(working ?? Blank(folder), new TemplateStore(folder));

    private static LabelTemplate Blank(string folder) => new()
    {
        Id = "user.b59", Name = "四条看样", WidthMm = 140, HeightMm = 100, PaddingMm = 4, BorderMm = 0.5,
    };

    // ---------- ① 条码拖角＝等比放大 ----------

    [Fact]
    public void DraggingTheBarcodeCornerScalesTheBarsInsteadOfStretchingThem() => OnStaThread(() =>
    {
        var vm = NewVm(TempFolder("corner"));
        vm.SnapEnabled = false;
        vm.AddBarcodeCommand.Execute(null);
        var bar = vm.Template.Elements.Single(e => e.Kind == ElementKind.Barcode);
        var (w0, h0) = (bar.Width, bar.Height);
        var module0 = vm.SampleLayout.Items.OfType<BarcodeItem>().Single().ModuleMm;

        vm.BeginDrag(bar.X + bar.Width, bar.Y + bar.Height, 2.5);          // 右下角柄
        vm.DragTo(bar.X + bar.Width * 2, bar.Y + bar.Height);              // 只往右拖一倍
        vm.EndDrag();

        Assert.True(bar.Width > w0 * 1.5, $"框宽没怎么变（{w0:0.#} → {bar.Width:0.#}）：这一拖没落到角柄上");
        // 等比 = 宽与高长的倍数一致。X 是整像素，吸附到"按 X 算出来的那只框"时会差一丝，留 0.12 的余量。
        Assert.True(Math.Abs(bar.Height / h0 - bar.Width / w0) < 0.12,
            $"角柄拖出来是 {bar.Width / w0:0.##}×宽、{bar.Height / h0:0.##}×高：两轴不同倍，不是等比");
        var module1 = vm.SampleLayout.Items.OfType<BarcodeItem>().Single().ModuleMm;
        Assert.True(module1 > module0 * 1.4,
            $"窄线只从 {module0:0.###} 变到 {module1:0.###} mm：又是拉长条不变粗（用户报的那件事）");
        Assert.True(bar.BarcodeSize!.ScalePercent > 100, "缩放比例没跟着框宽走：下次按 BoxOf 算就把这条打回原形");
    });

    [Fact]
    public void DraggingOnlyTheTopEdgeKeepsTheBarsAndChangesHeightOnly() => OnStaThread(() =>
    {
        // 同一只码只拖上下边＝向导那格「条形码高度」：条不该变粗。
        var vm = NewVm(TempFolder("edge"));
        vm.SnapEnabled = false;
        vm.AddBarcodeCommand.Execute(null);
        var bar = vm.Template.Elements.Single(e => e.Kind == ElementKind.Barcode);
        var module0 = vm.SampleLayout.Items.OfType<BarcodeItem>().Single().ModuleMm;
        var h0 = bar.Height;

        vm.BeginDrag(bar.X + bar.Width / 2, bar.Y, 2.5);                   // 上边中点
        vm.DragTo(bar.X + bar.Width / 2, bar.Y - 10);
        vm.EndDrag();

        Assert.True(bar.Height > h0, $"高没变（{h0:0.#} → {bar.Height:0.#}）：这一拖没落到上下边上");
        Assert.Equal(module0, vm.SampleLayout.Items.OfType<BarcodeItem>().Single().ModuleMm, 3);
    });

    // ---------- ② 形状工具排第一 ----------

    [Fact]
    public void TheShapeToolIsTheFirstButtonInTheToolbox() => OnStaThread(() =>
    {
        var vm = NewVm(TempFolder("toolbox"));
        var window = new TemplateEditorWindow(vm);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1180, 760));
        root.Arrange(new Rect(0, 0, 1180, 760));
        window.UpdateLayout();

        var box = (FrameworkElement)window.FindName("ToolboxBox")!;
        var shape = FindButton(box, "形状")!;
        Assert.NotNull(shape);
        var panel = (Panel)VisualTreeHelper.GetParent(shape)!;
        var first = FindFirstButton(panel);
        Assert.Equal("形状", first?.Content?.ToString());                  // 用户：「将形状工具调整到左列表第一个」
        var text = FindButton(box, "文本")!;
        Assert.True(panel.Children.IndexOf(first) == 0);
        Assert.NotSame(shape, text);
    });

    // ---------- ③ 编辑器用表里第一行的真条码 ----------

    [Fact]
    public void TheCanvasShowsTheFirstRowBarcodeOnceTheTableIsLoaded() => OnStaThread(() =>
    {
        var template = Blank("x");
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode, Text = "{{col:国际条码}}", Symbology = BarcodeSymbology.Ean13,
            ShowBarcodeText = true, X = 4, Y = 4, Width = 60, Height = 30, BarcodeSize = new BarcodeSizing(),
        });
        var vm = NewVm(TempFolder("record"), template);

        // 没表：样例把列名当值填进去 → EAN-13 当场判"只能编数字"（他截图里那句红字）。
        Assert.Contains("只能编", vm.SampleLayout.Items.OfType<BarcodeItem>().Single().Error);

        vm.PreviewRecord = MarkRecord.Builder()
            .SetRow(1, "订单.xlsx")
            .Set(MarkFieldKey.Consignee, "WALMART")
            .SetCustom("col:国际条码", "4006381333931")
            .Build();

        var item = vm.SampleLayout.Items.OfType<BarcodeItem>().Single();
        Assert.True(string.IsNullOrEmpty(item.Error), "递了表里第一行还是编不出来：" + item.Error);
        Assert.Equal("4006381333931", item.Data);
        Assert.NotEmpty(item.Bars);
    });

    // ---------- ④ 图层列表：最上面＝压在上面的 ----------

    [Fact]
    public void TheLayerListPutsTheTopmostObjectFirstAndDragRowsTranslateBack() => OnStaThread(() =>
    {
        var template = Blank("y");
        template.Elements.Add(new TemplateElement { Kind = ElementKind.Rect, X = 6, Y = 6, Width = 40, Height = 20 });
        template.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = "中间", X = 20, Y = 20, Width = 40, Height = 10 });
        template.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = "最上", X = 30, Y = 30, Width = 40, Height = 10 });
        var vm = NewVm(TempFolder("layers"), template);

        // 列表最上面那条必须是画在最上面的那个（CorelDRAW 对象管理器同口径）。
        Assert.Equal("最上", vm.LayerRows[0].Element.Text);
        Assert.Equal("中间", vm.LayerRows[1].Element.Text);
        Assert.Same(vm.Template.Elements[0], vm.LayerRows[2].Element);      // 落位 0 = 最底层 = 列表最下面

        // 把最底层那条（矩形）拖到列表第 0 行 → 它应当真的盖到最上面（数据层落到最后一个）。
        var bottom = vm.LayerRows[2].Element;
        Assert.True(vm.MoveLayerToRow(bottom, 0));
        Assert.Same(bottom, vm.Template.Elements[^1]);
        Assert.Same(bottom, vm.LayerRows[0].Element);
        Assert.Equal("最上", vm.Template.Elements[1].Text);                 // 原来最上的那条被挤下来一层
        Assert.False(vm.MoveLayerToRow(vm.LayerRows[1].Element, 1), "落在自己原位不该算一次移动（撤销里不留空步）");
    });

    // ---------- 帮助 ----------

    private static ButtonBase? FindButton(DependencyObject node, string content)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ButtonBase { Content: string s } b && s == content) return b;
            if (FindButton(child, content) is { } found) return found;
        }
        return null;
    }

    private static ButtonBase? FindFirstButton(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is ButtonBase) return (ButtonBase)child;
            if (FindFirstButton(child) is { } found) return found;
        }
        return null;
    }
}
