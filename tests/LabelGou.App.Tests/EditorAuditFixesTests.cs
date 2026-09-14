using System.IO;
using System.Windows;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core.Barcodes;
using LabelGou.Core.Editing;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 62 棒：编辑器 bug 大扫除（用户：「编辑层基本构建好了，这次再对编辑模板这部分检查是否存在 bug」）。
/// <para>三路只读审计报回 29 条疑点，逐条核代码后确认并修掉这一批；每条都配一判据——
/// <strong>先在没有修复的写法上会红，才允许算修过</strong>（量具自证）。没修的列在报告里，不当没看见。</para>
/// <para>这一批的共性是"每帧从快照重算"这一族（第 42/43/49 棒各踩过一次）：
/// <c>RestoreGeometry</c> 少恢复一个字段，误差就逐帧累积——这次漏的是第 58~61 棒新加的条码尺寸参数。</para>
/// </summary>
public class EditorAuditFixesTests
{
    private static void OnSta(Action work)
        => StaWorker.RunAsync((_, _) =>
        {
            work();
            return true;
        }, null, CancellationToken.None).GetAwaiter().GetResult();

    private static string Folder(string tag)
    {
        var f = Path.Combine(Path.GetTempPath(), "labelgou-b62-" + tag + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(f);
        return f;
    }

    private static LabelTemplate Template(string id = "user.b62") => new()
    {
        Id = id, Name = "审计看样", WidthMm = 140, HeightMm = 100, PaddingMm = 4, BorderMm = 0,
    };

    private static TemplateEditorViewModel NewVm(string tag, LabelTemplate? working = null)
        => new(working ?? Template(), new TemplateStore(Folder(tag)));

    // ---------- 1. 拖条码角：尺寸参数每帧从快照重算，不许逐帧自乘 ----------

    [Fact]
    public void DraggingABarcodeCornerDoesNotCompoundTheScaleFrameByFrame() => OnSta(() =>
    {
        var vm = NewVm("corner");
        vm.SnapEnabled = false;
        vm.AddBarcodeCommand.Execute(null);
        var bar = vm.Template.Elements.Single(e => e.Kind == ElementKind.Barcode);
        var (w0, h0, x0, y0) = (bar.Width, bar.Height, bar.X, bar.Y);

        vm.BeginDrag(x0 + w0, y0 + h0, 2.5);
        var trace = new System.Text.StringBuilder();
        for (var i = 1; i <= 8; i++)          // 同一笔手势里走 8 帧：从前每帧把缩放比例再乘一次
        {
            // 目标一律按按下那一刻的盒算（拿每帧在变的 bar.Height 当基准就是移动靶，测的不再是代码）
            vm.DragTo(x0 + w0 * (1 + 0.1 * i), y0 + h0 * (1 + 0.1 * i));
            trace.Append($"f{i}:W={bar.Width:0.##}/S={bar.BarcodeSize!.ScalePercent:0.#}  ");
        }
        vm.DragTo(x0 + w0 * 1.8, y0 + h0 * 1.8);   // 同一目标再拖一次：真从快照重算的话宽不该再变
        trace.Append($"重复:W={bar.Width:0.##}/S={bar.BarcodeSize!.ScalePercent:0.#}  ");
        vm.EndDrag();

        var wanted = 1.8;                      // 最后一帧是 1.8 倍宽，缩放比例就该是 1.8 倍
        Assert.True(Math.Abs(bar.BarcodeSize!.ScalePercent / 100 - wanted) < 0.05,
            $"拖完缩放比例成了 {bar.BarcodeSize.ScalePercent:0.#} %（该是 {wanted:0.##} 倍）：{trace}（w0={w0:0.##}）");
        Assert.True(bar.Height <= vm.Template.HeightMm + 0.001,
            $"框高 {bar.Height:0.#} mm 超出标签 {vm.Template.HeightMm} mm：拖角把框顶出纸外");
    });

    // ---------- 2. 画到一半撤销：不写进无关元素，画布也不再越界抛 ----------

    [Fact]
    public void UndoWhileDrawingDoesNotWriteNodesIntoAnotherElement() => OnSta(() =>
    {
        var template = Template();
        template.Elements.Add(new TemplateElement { Kind = ElementKind.Rect, X = 5, Y = 5, Width = 20, Height = 12 });
        var vm = NewVm("path", template);
        var rect = vm.Template.Elements[0];

        vm.IsBezierTool = true;
        vm.BeginPath(60, 60);                  // 落下第一个点（新建那条线，下标 1）
        vm.Undo();                             // 撤销把元素换掉/删掉，下标已经不是我那一条
        Assert.True(vm.IsBezierTool);
        vm.DragPath(70, 70);                   // 从前这一步会把节点写进占着那个下标的无关元素
        vm.EndPathSegment();

        Assert.Equal(0, rect.Nodes?.Count ?? 0);
        Assert.True(vm.Template.Elements.All(e => e.Kind != ElementKind.Line || e.Nodes is not { Count: > 0 }
                                                 || e.X > 0), "画到一半撤销后，节点被写进了别的元素");
    });

    [Fact]
    public void CanvasStillRendersAfterThePathElementIsGone() => OnSta(() =>
    {
        var vm = NewVm("render");
        var window = new TemplateEditorWindow(vm);
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1180, 760));
        root.Arrange(new Rect(0, 0, 1180, 760));
        window.UpdateLayout();

        vm.IsBezierTool = true;
        vm.BeginPath(30, 30);
        vm.Undo();                             // PathIndex 还指着那条，元素却已经没了
        vm.IsBezierTool = false;
        window.UpdateLayout();                 // 从前这里 OnRender 直接索引越界，且每次重绘再抛一次
        root.InvalidateVisual();
        window.UpdateLayout();
        Assert.True(true);                     // 没抛就是过
    });

    // ---------- 3. 撤销之后选中还停在同一层 ----------

    [Fact]
    public void UndoKeepsTheSelectionOnTheSameLayer() => OnSta(() =>
    {
        var template = Template();
        template.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = "第一层", X = 6, Y = 6, Width = 40, Height = 10 });
        template.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = "第二层", X = 6, Y = 20, Width = 40, Height = 10 });
        var vm = NewVm("sel", template);
        vm.SelectedRow = vm.Elements[0];

        vm.BeginDrag(26, 11, 2.5);
        vm.DragTo(36, 11);
        vm.EndDrag();
        vm.Undo();

        Assert.NotNull(vm.SelectedRow);
        Assert.Equal(0, vm.SelectedRow!.Ordinal);              // 还在第一层，不是被甩到最后一行
        Assert.Equal("第一层", vm.SelectedRow.Element.Text);
    });

    // ---------- 4. 只点一下选元素：不算改动、不吃撤销步 ----------

    [Fact]
    public void AClickThatMovesNothingMarksNeitherDirtyNorAnUndoStep() => OnSta(() =>
    {
        var template = Template();
        template.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = "别动我", X = 10, Y = 10, Width = 40, Height = 10 });
        template.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = "也别动我", X = 10, Y = 26, Width = 40, Height = 10 });
        var vm = NewVm("click", template);
        vm.SnapEnabled = false;
        Assert.False(vm.IsDirty);                              // 起点是干净的

        vm.BeginDrag(30, 15, 2.5);                             // 只点一下选中它
        vm.DragTo(30, 15);
        vm.EndDrag();

        Assert.False(vm.IsDirty);                              // 没改东西就不该问"要不要保存"
        Assert.False(vm.UndoCommand.CanExecute(null));         // 也不该留下一步空撤销
    });

    // ---------- 5. 换真值之后，问题列表跟着按同一份重量 ----------

    [Fact]
    public void HandingInTheFirstRowRefreshesTheIssueListToo() => OnSta(() =>
    {
        var template = Template();
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "{{col:货号}}", X = 100, Y = 4, Width = 60, Height = 10,
            FontSizePt = 12, ShrinkToFit = false, MaxLines = 1,
        });
        var vm = NewVm("record", template);
        vm.PreviewRecord = MarkRecord.Builder()
            .SetRow(1, "订单.xlsx").Set(MarkFieldKey.Consignee, "WALMART")
            .SetCustom("col:货号", "ABCDEFGABCDEFGABCDEFGABCDEFG").Build();

        Assert.Contains(vm.Issues, m => m.Contains("探出标签", StringComparison.Ordinal));   // 画布按真值画，列表也得按真值判
    });

    // ---------- 6. 存盘之后墨迹那条不许凭空消失 ----------

    [Fact]
    public void SavingDoesNotDropTheInkOverflowWarning() => OnSta(() =>
    {
        var template = Template("user.b62.ink");
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "一行很长很长的字排到纸外面去了看看能不能存下来", X = 100, Y = 4,
            Width = 120, Height = 12, FontSizePt = 12, ShrinkToFit = false, MaxLines = 1,
        });
        var vm = NewVm("ink", template);
        vm.AskSkipIssues = _ => true;                          // 用户点「跳过」

        vm.SaveCommand.Execute(null);

        Assert.Contains(vm.Issues, m => m.Contains("探出标签", StringComparison.Ordinal));
    });
}
