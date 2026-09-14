using System.IO;
using System.Windows;
using System.Windows.Input;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core.Editing;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 63 棒：编辑器交互五条（用户 2026-09-14 一次列了六条，第 3 条口径待他确认）。
/// ①「文字出现左中及后半段可以移动右半段无法移动」②「有些时候使用 delete 键不触发删除」
/// ④「移动元素时鼠标该变成上下左右那个标志」⑤「形状工具变成编辑工具（普通鼠标形态）」
/// ⑥「按住 shift 移动时为水平/垂直方向移动」。
/// </summary>
public class EditorInteractionFixesTests
{
    private static void OnSta(Action work)
        => StaWorker.RunAsync((_, _) =>
        {
            work();
            return true;
        }, null, CancellationToken.None).GetAwaiter().GetResult();

    private static string Folder(string tag)
    {
        var f = Path.Combine(Path.GetTempPath(), "labelgou-b63-" + tag + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(f);
        return f;
    }

    private static TemplateEditorViewModel NewVm(string tag, LabelTemplate working)
        => new(working, new TemplateStore(Folder(tag)));

    private static LabelTemplate OneText(string text, double widthMm)
    {
        var t = new LabelTemplate { Id = "user.b63", Name = "交互看样", WidthMm = 140, HeightMm = 100, PaddingMm = 4, BorderMm = 0 };
        t.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = text, X = 4, Y = 10, Width = widthMm, Height = 12,
            FontSizePt = 12, ShrinkToFit = false, MaxLines = 1,
        });
        return t;
    }

    // ---------- ① 字排到盒外那一段也要抓得住 ----------

    [Fact]
    public void ThePartOfTheTextThatHangsOutsideItsBoxIsStillGrabbable() => OnSta(() =>
    {
        var vm = NewVm("hit", OneText("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 30));
        var row = vm.Template.Elements[0];
        var ink = vm.DisplayBoxOf(row);
        Assert.NotNull(ink);
        Assert.True(ink!.Value.Width > row.Width + 20, $"墨迹只有 {ink!.Value.Width:0.#} mm，没排出盒外：这条测不到东西");

        var xInInkButOutsideBox = ink!.Value.X + ink.Value.Width - 2;      // 墨迹最右那两个字，排版盒早就到头了
        Assert.True(xInInkButOutsideBox > row.X + row.Width, "选的点没落到盒外，测不到这一条");

        var mode = vm.BeginDrag(xInInkButOutsideBox, ink.Value.Y + ink.Value.Height / 2, 1.5);
        Assert.NotEqual(TemplateEditorViewModel.DragMode.None, mode);       // 从前这里抓不住 → "右半段无法移动"
        Assert.Same(row, vm.SelectedRow?.Element);
    });

    // ---------- ⑤ 「编辑」就是默认那一档 ----------

    [Fact]
    public void TheEditToolIsTheDefaultOne() => OnSta(() =>
    {
        var vm = NewVm("tool", OneText("默认档", 40));
        Assert.True(vm.IsNodeTool);                                          // 打开编辑器不用先点一颗按钮
        Assert.Equal(TemplateEditorViewModel.EditorTool.Shape, vm.Tool);
    });

    // ---------- ⑥ Shift 移动 = 只沿一根轴 ----------

    /// <summary>用一个矩形测轴锁：文本的占位盒（墨迹）会参与贴边钳制，拿它测会被那条规则干扰出假绿。</summary>
    private static LabelTemplate OneRect()
    {
        var t = new LabelTemplate { Id = "user.b63r", Name = "轴锁看样", WidthMm = 140, HeightMm = 100, PaddingMm = 4, BorderMm = 0 };
        t.Elements.Add(new TemplateElement { Kind = ElementKind.Rect, X = 20, Y = 20, Width = 40, Height = 20, ThicknessMm = 0.6 });
        return t;
    }

    [Fact]
    public void ShiftWhileMovingLocksToHorizontalOrVertical() => OnSta(() =>
    {
        var vm = NewVm("shift", OneRect());
        vm.SnapEnabled = false;
        var row = vm.Template.Elements[0];
        var (cx, cy) = (row.X + row.Width / 2, row.Y + row.Height / 2);

        vm.BeginDrag(cx, cy, 1.5, ResizeAnchor.Opposite, lockAxis: false, shiftHeld: true);
        vm.DragTo(cx + 20, cy + 15);
        vm.EndDrag();

        Assert.Equal(cx + 20, row.X + row.Width / 2, 1);                     // 位移更大的那根轴跟着走
        Assert.Equal(cy, row.Y + row.Height / 2, 1);                         // 另一根一动不动
    });

    [Fact]
    public void WithoutShiftTheMoveIsFreeInBothAxes() => OnSta(() =>
    {
        var vm = NewVm("noshift", OneRect());
        vm.SnapEnabled = false;
        var row = vm.Template.Elements[0];
        var (cx, cy) = (row.X + row.Width / 2, row.Y + row.Height / 2);

        vm.BeginDrag(cx, cy, 1.5);
        vm.DragTo(cx + 20, cy + 15);
        vm.EndDrag();

        Assert.Equal(cx + 20, row.X + row.Width / 2, 1);                     // 反证：不按住 Shift 两轴都走
        Assert.Equal(cy + 15, row.Y + row.Height / 2, 1);                    // 不然上面那条就是假绿
    });

    // ---------- ③ Ctns 那行不能因为"来源递错"而整条消失 ----------

    [Fact]
    public void TheCtnsRowOnlyAppearsWhenTheRecordCarriesTheComputedCartonCount() => OnSta(() =>
    {
        var template = BuiltInTemplates.RowsFour140x100();

        // 只差一项：有没有 {{col:本行箱数}} 这个推算量（表里第一行没有，编号后的标签才有）。
        var noCount = MarkRecord.Builder()
            .SetRow(1, "订单.xlsx")
            .Set(MarkFieldKey.Consignee, "WALMART").Set(MarkFieldKey.ItemNo, "AJ7-001").Set(MarkFieldKey.Quantity, "12")
            .Build();
        var withCount = MarkRecord.Builder()
            .SetRow(1, "订单.xlsx")
            .Set(MarkFieldKey.Consignee, "WALMART").Set(MarkFieldKey.ItemNo, "AJ7-001").Set(MarkFieldKey.Quantity, "12")
            .SetCustom("col:本行箱数", "5")
            .Build();

        var rowsWithout = CountRows(template, noCount);
        var rowsWith = CountRows(template, withCount);

        Assert.True(rowsWith == rowsWithout + 1 && rowsWithout >= 3,
            $"没推算量 {rowsWithout} 行、有推算量 {rowsWith} 行——那一行会整条无声消失（他看到的 Ctns 不显示）");
    });

    private static int CountRows(LabelTemplate template, MarkRecord record)
        => LayoutEngine.Build(template, record, new LayoutContext(1, 1, "体检.xlsx"))
            .Items.OfType<LabelGou.Core.Layout.TextItem>().Count();

    /// <summary>第 66 棒：占位符在预览那条数据里取不到值时必须当场点名——不能一边留个空框，一边写「目前没有问题，可以保存」。</summary>
    [Fact]
    public void TheEditorNamesAnyRowThatWouldPrintEmpty() => OnSta(() =>
    {
        var template = new LabelTemplate { Id = "user.b66", Name = "空行点名看样", WidthMm = 140, HeightMm = 100, PaddingMm = 4, BorderMm = 0 };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "Ctns：{{col:本行箱数}}件", X = 6, Y = 6, Width = 60, Height = 12, FontSizePt = 12,
        });
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "托盘：{{col:这一列根本不存在}}", X = 6, Y = 24, Width = 60, Height = 12, FontSizePt = 12,
        });
        var vm = NewVm("empty", template);
        // 像真表的一条记录：有推算量本行箱数，没有那个列名。
        // （不递记录时 TemplateSample 会给每个 col 令牌都补上列名，永远"有值"，那样这条判据测不到东西。）
        vm.PreviewRecord = MarkRecord.Builder()
            .SetRow(1, "订单.xlsx").Set(MarkFieldKey.Consignee, "WALMART")
            .SetCustom("col:本行箱数", "5")
            .Build();

        Assert.True(vm.Issues.Any(m => m.Contains("{{col:这一列根本不存在}}", StringComparison.Ordinal)
                                       && m.Contains("没有值", StringComparison.Ordinal)),
            $"Issues=[{string.Join("||", vm.Issues)}]");
        Assert.DoesNotContain(vm.Issues, m => m.Contains("{{col:本行箱数}}", StringComparison.Ordinal));   // 有值的那一行不该被误报
    });

    [Fact]
    public void DeleteWorksThroughTheWindowEvenWhenTheCanvasHasNoFocus() => OnSta(() =>
    {
        var vm = NewVm("del", OneText("删我", 40));
        var window = new TemplateEditorWindow(vm);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1180, 760));
        root.Arrange(new Rect(0, 0, 1180, 760));
        window.UpdateLayout();

        vm.SelectedRow = vm.Elements[0];
        // 焦点在图层列表上（用户就是从那里点的元素）——画布的 KeyDown 收不到，Delete 必须还能删
        window.LayerListForTests.Focus();
        Assert.True(window.EditorCanvas.TryDeleteKey());
        Assert.Empty(vm.Template.Elements);
    });

    // ---------- ⑦ 第 67 棒：滚轮三档视角手势 ----------

    [Fact]
    public void WheelGesturesMapToZoomHorizontalAndVertical()
    {
        var (zx, zy, zf) = Rendering.TemplateEditorControl.WheelGesture(ModifierKeys.None, 120);
        Assert.Equal((0d, 0d), (zx, zy));
        Assert.True(zf > 1, "裸轮向上该放大");
        Assert.True(Rendering.TemplateEditorControl.WheelGesture(ModifierKeys.None, -120).ZoomFactor < 1, "裸轮向下该缩小");

        var step = Rendering.TemplateEditorControl.PanStepDiu;
        Assert.Equal((step, 0d, 1d), Rendering.TemplateEditorControl.WheelGesture(ModifierKeys.Control, 120));
        Assert.Equal((-step, 0d, 1d), Rendering.TemplateEditorControl.WheelGesture(ModifierKeys.Control, -120));
        Assert.Equal((0d, -step, 1d), Rendering.TemplateEditorControl.WheelGesture(ModifierKeys.Alt, 120));
        Assert.Equal((0d, step, 1d), Rendering.TemplateEditorControl.WheelGesture(ModifierKeys.Alt, -120));
    }

    [Fact]
    public void PanningMovesTheLabelInsideTheCanvas() => OnSta(() =>
    {
        var vm = NewVm("pan", OneRect());
        var window = new TemplateEditorWindow(vm);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1180, 760));
        root.Arrange(new Rect(0, 0, 1180, 760));
        window.UpdateLayout();

        var before = window.EditorCanvas.CurrentOrigin;
        window.EditorCanvas.PanBy(60, -40);
        window.UpdateLayout();
        var after = window.EditorCanvas.CurrentOrigin;

        Assert.Equal(before.X + 60, after.X, 1);
        Assert.Equal(before.Y - 40, after.Y, 1);
    });
}
