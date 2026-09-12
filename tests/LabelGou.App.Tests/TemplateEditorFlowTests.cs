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
        var text = vm.Template.Elements[0];
        var inkWidth = vm.DisplayBoxOf(text)!.Value.Width;

        vm.BeginDrag(FirstCenterX, FirstCenterY, 0.3);
        // 第 46 棒：吸的是**看得见的墨迹**那条中线。拖到"墨迹中心正好落在纸的垂直中线"上，
        // 吸附应当命中并把它钉在 50（旧口径吸的是 20mm 排版盒的中线，落点会是 40）。
        vm.DragTo(60 - inkWidth / 2, FirstCenterY);

        Assert.Equal(GuideSource.LabelCenter, Assert.Single(vm.ActiveGuides).Source);
        var snapped = vm.DisplayBoxOf(text)!.Value;
        Assert.Equal(50, snapped.X + snapped.Width / 2, 3);
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

        // 第 46 棒：停住的边界 = 看得见的墨迹边（旧口径停在排版盒边 X=80，可那时字看着离右边还远）
        var ink = vm.DisplayBoxOf(vm.Template.Elements[0])!.Value;
        Assert.Equal(100, ink.X + ink.Width, 3);
        Assert.Equal(80, ink.Y + ink.Height, 3);
    });

    [Fact]
    public void NonTextStillStopsByItsOwnBox() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SnapEnabled = false;
        var rect = vm.Template.Elements[1];      // 矩形 30×12：它的盒 == 看得见的东西，口径不变

        vm.BeginDrag(rect.X + rect.Width / 2, rect.Y + rect.Height / 2, 0.3);
        vm.DragTo(500, 500);
        vm.EndDrag();

        Assert.Equal(70, rect.X, 6);
        Assert.Equal(68, rect.Y, 6);
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

    /// <summary>第 43 棒：逐层移动。原来只有「置最上/置最下」一步到顶，用户报"图层不能上移下移被固定"。</summary>
    [Fact]
    public void MoveUpAndDownShiftExactlyOneLayer() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.AddTextCommand.Execute(null);   // Working 有 2 个元素，再加一个 = 3
        Assert.Equal(3, vm.Template.Elements.Count);

        vm.SelectedRow = vm.Elements[0];
        var picked = vm.SelectedRow!.Element;

        vm.MoveUpCommand.Execute(null);
        Assert.Equal(1, vm.Template.Elements.IndexOf(picked));
        Assert.Same(picked, vm.SelectedRow!.Element);          // 换层后选中不丢
        Assert.Contains("上移一层", vm.StatusText);

        vm.MoveUpCommand.Execute(null);
        Assert.Equal(2, vm.Template.Elements.IndexOf(picked));

        // 到顶再点：不动，但要说明白，不许静默（§五-80 同族：没人读到的判据等于没判据）
        vm.MoveUpCommand.Execute(null);
        Assert.Equal(2, vm.Template.Elements.IndexOf(picked));
        Assert.Contains("已经在最上层", vm.StatusText);

        vm.MoveDownCommand.Execute(null);
        Assert.Equal(1, vm.Template.Elements.IndexOf(picked));
        Assert.Contains("下移一层", vm.StatusText);
    });

    // ---------- 第 44 棒：选中框贴文字墨迹（用户圈的红框），不再框整条行带 ----------

    [Fact]
    public void TextDisplayBoxIsTighterThanTheLayoutBox() => OnStaThread(() =>
    {
        // Working 的第一元素：文本"顶部标题"，排版盒宽 20mm；四个汉字 8pt 实际墨迹远窄于 20mm。
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        var text = vm.Template.Elements[0];

        var box = vm.DisplayBoxOf(text);

        Assert.NotNull(box);
        var (x, y, w, h) = box!.Value;
        Assert.True(w < text.Width - 1, $"墨迹宽 {w:F1} 该明显窄于排版盒 {text.Width}（否则等于没贴字）");
        Assert.True(h > 0.5 && h < text.Height + 0.5, $"墨迹高 {h:F1} 该落在字高量级，不是行带 {text.Height}");
        // 左对齐：墨迹左边≈排版盒左边（±容差），不能跑到盒外
        Assert.True(Math.Abs(x - text.X) < 1.5, $"左对齐墨迹左边 {x:F1} 应贴着盒左边 {text.X}");
    });

    [Fact]
    public void NonTextElementHasNoDisplayBoxOverride() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        var rect = vm.Template.Elements[1];   // Working 第二个是矩形

        Assert.Null(vm.DisplayBoxOf(rect));   // 矩形退回默认 VisualBoxOf
    });

    [Fact]
    public void DisplayBoxGrowsWithTextStretch() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        var text = vm.Template.Elements[0];

        var before = vm.DisplayBoxOf(text)!.Value.Width;
        text.TextScaleX = 2;
        vm.RebuildSample();                   // 拉伸变了，缓存要重算
        var after = vm.DisplayBoxOf(text)!.Value.Width;

        Assert.True(after > before * 1.7, $"抻两倍墨迹宽没跟上：{before:F1}→{after:F1}");
    });

    // ---------- 第 45 棒：缩放锚点照 CDR——墨迹那块不许被拖走（用户报的"每次缩放都偏移"） ----------

    /// <summary>
    /// 一份带字面拉伸的文本（照图一那家 AI 行式模板的真形状）：墨迹比排版盒窄，
    /// 且墨迹左边 ≠ 盒左边——正是"绕排版盒中心长"会把左上角搬走的那个形状。
    /// </summary>
    private static LabelTemplate StretchyText() => new()
    {
        Id = "user.anchor",
        Name = "锚点测试",
        WidthMm = 100,
        HeightMm = 80,
        PaddingMm = 4,
        Elements =
        {
            new TemplateElement
            {
                Kind = ElementKind.Text, Text = "顶部标题", X = 4, Y = 24, Width = 60, Height = 10,
                FontSizePt = 14.3, TextScaleX = 0.763, TextScaleY = 0.763,
            },
        },
    };

    [Fact]
    public void CornerDragKeepsTheInkTopLeftInPlace() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], StretchyText());
        vm.SnapEnabled = false;
        var text = vm.Template.Elements[0];
        var before = vm.DisplayBoxOf(text)!.Value;
        var fontBefore = text.FontSizePt;

        // 从墨迹的右下角起手（用户抓的就是这块），往右下拖 6mm
        Assert.Equal(TemplateEditorViewModel.DragMode.Resize,
            vm.BeginDrag(before.X + before.Width, before.Y + before.Height, 0.4));
        vm.DragTo(before.X + before.Width + 6, before.Y + before.Height + 6);
        vm.EndDrag();

        var after = vm.DisplayBoxOf(text)!.Value;
        Assert.True(text.FontSizePt > fontBefore, $"字号该跟着角柄变大：{fontBefore}→{text.FontSizePt}");
        Assert.True(Math.Abs(after.X - before.X) < 0.1, $"墨迹左边漂了 {(after.X - before.X):F3}mm（左上角必须钉住）");
        Assert.True(Math.Abs(after.Y - before.Y) < 0.1, $"墨迹顶边漂了 {(after.Y - before.Y):F3}mm（左上角必须钉住）");
        Assert.True(after.Width > before.Width, $"墨迹宽没长：{before.Width:F1}→{after.Width:F1}");
    });

    [Fact]
    public void ShiftCornerDragGrowsAroundTheInkCenter() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], StretchyText());
        vm.SnapEnabled = false;
        var text = vm.Template.Elements[0];
        var before = vm.DisplayBoxOf(text)!.Value;

        Assert.Equal(TemplateEditorViewModel.DragMode.Resize, vm.BeginDrag(
            before.X + before.Width, before.Y + before.Height, 0.4, ResizeAnchor.Center));
        vm.DragTo(before.X + before.Width + 6, before.Y + before.Height + 6);
        vm.EndDrag();

        var after = vm.DisplayBoxOf(text)!.Value;
        var (cxBefore, cyBefore) = (before.X + before.Width / 2, before.Y + before.Height / 2);
        var (cxAfter, cyAfter) = (after.X + after.Width / 2, after.Y + after.Height / 2);
        Assert.True(Math.Abs(cxAfter - cxBefore) < 0.1, $"中心 X 漂了 {(cxAfter - cxBefore):F3}mm（Shift 就该绕中心）");
        Assert.True(Math.Abs(cyAfter - cyBefore) < 0.1, $"中心 Y 漂了 {(cyAfter - cyBefore):F3}mm（Shift 就该绕中心）");
        // 中心不动而墨迹变大 → 左上角必然往左上走，这正是"向四周放大"的样子
        Assert.True(after.X < before.X, $"左上角该让开：{before.X:F1}→{after.X:F1}");
    });

    [Fact]
    public void EdgeDragHoldsTheOppositeInkEdge() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], StretchyText());
        vm.SnapEnabled = false;
        var text = vm.Template.Elements[0];
        var before = vm.DisplayBoxOf(text)!.Value;

        // 拖墨迹的右边中点：渲染是绕盒中心抻（对面边会对称让开），锚点校正要把左边钉回原地
        Assert.Equal(TemplateEditorViewModel.DragMode.Resize,
            vm.BeginDrag(before.X + before.Width, before.Y + before.Height / 2, 0.4));
        vm.DragTo(before.X + before.Width + 8, before.Y + before.Height / 2);
        vm.EndDrag();

        var after = vm.DisplayBoxOf(text)!.Value;
        Assert.True(Math.Abs(after.X - before.X) < 0.1, $"对面边漂了 {(after.X - before.X):F3}mm");
        Assert.True(after.Width > before.Width + 4, $" grabbed 边没跟上：{before.Width:F1}→{after.Width:F1}");
        Assert.True(Math.Abs(text.FontSizePt - 14.3) < 1e-6, "拖边是抻字身，字号不该动");
    });

    [Fact]
    public void CtrlMoveLocksToTheDominantAxis() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], StretchyText());
        vm.SnapEnabled = false;
        var text = vm.Template.Elements[0];
        var box = vm.DisplayBoxOf(text)!.Value;
        var insideX = box.X + box.Width / 2;
        var insideY = box.Y + box.Height / 2;

        Assert.Equal(TemplateEditorViewModel.DragMode.Move,
            vm.BeginDrag(insideX, insideY, 0.2, ResizeAnchor.Opposite, lockAxis: true));
        vm.DragTo(insideX + 7, insideY + 1);      // 水平拖得多 → 只许走水平

        vm.EndDrag();
        Assert.Equal(11, text.X, 6);              // 4 + 7：水平轴照走
        Assert.Equal(24, text.Y, 6);              // 这一轴被 Ctrl 锁住，一个字没动
    });

    [Fact]
    public void AlignCommandsMoveAgainstTheLabel() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6]);
        vm.SelectedRow = vm.Elements[0];
        var element = vm.Template.Elements[0];
        var yBefore = element.Y;

        // 单轴：点「水平居中」只该动 X。上一版把 Y 也甩到 0（想居中就得重贴一次顶）
        vm.AlignCommand.Execute("Center");
        Assert.Equal(50, InkCenterX(vm, element), 3);         // 墨迹水平居中（旧口径居中的是 20mm 排版盒）
        Assert.Equal(yBefore, element.Y, 6);

        vm.AlignCommand.Execute("Top");
        Assert.Equal(50, InkCenterX(vm, element), 3);         // 顶对齐也不能反过来改水平位置
        Assert.Equal(0, vm.DisplayBoxOf(element)!.Value.Y, 3); // 贴的是字的上缘，不是行带的上缘

        vm.PaddingCommand.Execute("Bottom");
        var ink = vm.DisplayBoxOf(element)!.Value;
        Assert.Equal(4, ink.X, 3);                            // 贴左内边距线
        Assert.Equal(76, ink.Y + ink.Height, 3);              // 贴下内边距线（80 − 4）
    });

    [Fact]
    public void ShortTextWithAFullWidthBandReachesTheRightEdge() => OnStaThread(() =>
    {
        // 用户实测形状（11 份 AI 版式全是这个数）：纸 140、行带 130、这一行的字只有约 20mm 宽。
        var paper = new LabelTemplate
        {
            Id = "user.band", Name = "行带顶手", WidthMm = 140, HeightMm = 100, PaddingMm = 5,
            Elements = { new TemplateElement { Kind = ElementKind.Text, Text = "新文本", X = 5, Y = 20, Width = 130, Height = 10, FontSizePt = 14.3 } },
        };
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], paper);
        vm.SnapEnabled = false;
        var text = vm.Template.Elements[0];
        var before = vm.DisplayBoxOf(text)!.Value;

        vm.BeginDrag(before.X + before.Width / 2, before.Y + before.Height / 2, 0.3);
        vm.DragTo(before.X + before.Width / 2 + 500, before.Y + before.Height / 2);
        vm.EndDrag();

        var after = vm.DisplayBoxOf(text)!.Value;
        Assert.True(after.X + after.Width > 139,
            $"墨迹右缘只到 {after.X + after.Width:F1}——还被那条 130mm 隐形行带顶在 X=10 的墙上");
        Assert.True(after.X + after.Width <= 140.001, "贴边停住仍要生效，墨迹不许被拖出纸外");
        // 这条钉的是**同棒必须一起改的那一半**：校验器还在按行带判越界的话，
        // 字拖到右边 = 行带探出纸 = Error = 存不了盘，等于把一堵墙换成一条死路。
        Assert.False(vm.HasError, "摆位放开后，校验器不能再拿虚拟行带当占物判越界");
    });

    [Fact]
    public void DraggingMovesMonotonicallyAndStopsOnTheEdge() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], StretchyText());
        vm.SnapEnabled = false;
        var text = vm.Template.Elements[0];
        var ink = vm.DisplayBoxOf(text)!.Value;

        vm.BeginDrag(ink.X + ink.Width / 2, ink.Y + ink.Height / 2, 0.3);
        var previous = text.X;
        for (var step = 1; step <= 8; step++)
        {
            vm.DragTo(ink.X + ink.Width / 2 + step * 20, ink.Y + ink.Height / 2);
            Assert.True(text.X >= previous - 0.001,
                $"第 {step} 帧往回走了：{previous:F2} → {text.X:F2}（钳制基准读到了上一帧的墨迹盒 = 抖动）");
            previous = text.X;
        }
        vm.EndDrag();

        var settled = vm.DisplayBoxOf(text)!.Value;
        Assert.Equal(100, settled.X + settled.Width, 2);   // 贴住纸边就停在那，不再来回弹
    });

    /// <summary>墨迹比纸还宽的一行（长值出纸）必须还能自由拖：夹住 = 钉死在原地，就是用户报的"被限制"。</summary>
    [Fact]
    public void OverwideLineStillDragsFreely() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], OverflowingText());
        vm.SnapEnabled = false;
        var text = vm.Template.Elements[0];
        var ink = vm.DisplayBoxOf(text)!.Value;
        Assert.True(ink.Width > 100, "夹具得真的比纸宽，否则这条测不到东西");

        vm.BeginDrag(ink.X + 2, ink.Y + ink.Height / 2, 0.3);
        vm.DragTo(ink.X + 2 + 30, ink.Y + ink.Height / 2);
        vm.EndDrag();

        Assert.Equal(34, text.X, 3);     // 4 + 30：跟着手走，没有被夹回 0
    });

    private static double InkCenterX(TemplateEditorViewModel vm, LabelGou.Core.Templates.TemplateElement element)
    {
        var box = vm.DisplayBoxOf(element)!.Value;
        return box.X + box.Width / 2;
    }

    // ---------- 第 46 棒：行带的保护交出去之后，由"墨迹越界"这道闸接手 ----------

    /// <summary>一条 40mm 宽、永不折行的长文字——22 个字约 111mm 墨迹，从 X=4 排出去必然超过 100mm 的纸。</summary>
    private static LabelTemplate OverflowingText() => new()
    {
        Id = "user.over", Name = "出纸的一行", WidthMm = 100, HeightMm = 80, PaddingMm = 4,
        Elements =
        {
            new TemplateElement { Kind = ElementKind.Text, Text = "深圳市顺达贸易有限公司收货人全称核对一下长度", X = 4, Y = 20, Width = 40, Height = 10, FontSizePt = 14.3 },
        },
    };

    [Fact]
    public void NoWrapLongValueRunsOffThePaperAndBlocksSaving() => OnStaThread(() =>
    {
        var dir = Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6];
        var vm = NewVm(dir, OverflowingText());
        var text = vm.Template.Elements[0];

        var ink = vm.DisplayBoxOf(text)!.Value;
        Assert.True(ink.Width > text.Width,
            $"永不折行的墨迹宽 {ink.Width:F1} 被夹在盒宽 {text.Width} 内 = 越界被藏起来了");
        Assert.True(vm.HasError, "字排到纸外必须让编辑器报 Error（Core 量不到墨迹，只能由这一侧把闸）");

        vm.Save();
        Assert.Equal(0, new TemplateStore(dir).ListAll().Count(t => !t.BuiltIn));   // 带病模板不许进库
    });

    [Fact]
    public void ShrinkIntoLabelPullsTheLineBackInside() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], OverflowingText());
        vm.SelectedRow = vm.Elements[0];
        var text = vm.Template.Elements[0];
        Assert.True(vm.HasError);
        var fontBefore = text.FontSizePt;

        vm.ShrinkIntoLabelCommand.Execute(null);

        Assert.False(vm.HasError, "缩完不该再有越界 Error");
        Assert.True(text.FontSizePt < fontBefore, $"字号没缩：{fontBefore}→{text.FontSizePt}");
        var ink = vm.DisplayBoxOf(text)!.Value;
        Assert.True(ink.X >= 3.9 && ink.X + ink.Width <= 100.1,
            $"墨迹 {ink.X:F1}~{ink.X + ink.Width:F1} 还没回到纸内");
    });

    /// <summary>
    /// 出纸前那道闸的输入端：真数据排出来的墨迹越界要数得出张数（只测文案不算数，闸门拿的是这个数）。
    /// </summary>
    [Fact]
    public void RealDataOverflowIsCountedForThePrintGate() => OnStaThread(() =>
    {
        var records = new[] { LabelGou.Core.Layout.SampleRecords.StandardSample(), LabelGou.Core.Layout.SampleRecords.StandardSample() };
        LabelTemplate Paper(double widthMm) => new()
        {
            Id = "user.real", Name = "真数据越界", WidthMm = widthMm, HeightMm = 80, PaddingMm = 4, BorderMm = 0,
            Elements = { new TemplateElement { Kind = ElementKind.Text, Text = "{{Consignee}}", X = 4, Y = 20, Width = 40, Height = 10, FontSizePt = 14.3 } },
        };

        var narrow = new LabelGou.App.Export.PageContentSource(Paper(60), records, "真数据.xlsx");
        Assert.Equal(2, narrow.InkOverflowLabelCount);          // 样例收货人约 71mm 墨迹，60mm 的纸每张都出纸
        Assert.False(narrow.InkScanTruncated);

        var wide = new LabelGou.App.Export.PageContentSource(Paper(200), records, "真数据.xlsx");
        Assert.Equal(0, wide.InkOverflowLabelCount);            // 纸够宽就不该误报
    });

    [Fact]
    public void ShrinkIntoLabelSaysSoWhenThereIsNothingToShrink() => OnStaThread(() =>
    {
        var vm = NewVm(Path.GetTempPath() + Guid.NewGuid().ToString("N")[..6], OverflowingText());
        vm.SelectedRow = vm.Elements[0];
        vm.ShrinkIntoLabelCommand.Execute(null);
        var statusAfterRealShrink = vm.StatusText;

        vm.ShrinkIntoLabelCommand.Execute(null);       // 再点一次：已经在纸内了

        Assert.Contains("缩回纸内", statusAfterRealShrink);
        Assert.Contains("已经在纸内", vm.StatusText);   // 第二次不许再偷偷缩一次字号，只把实情说一句
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
