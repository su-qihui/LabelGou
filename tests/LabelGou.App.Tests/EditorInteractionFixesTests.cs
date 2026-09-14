using System.IO;
using System.Windows;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core.Editing;
using LabelGou.Core.Layout;
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

    // ---------- ② Delete 不认焦点 ----------

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
}
