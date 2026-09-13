using System.IO;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core.Colors;
using LabelGou.Core.Editing;
using LabelGou.Core.Layout;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 50 棒第一刀：矩形框改成"按下拖出一只框"（CorelDRAW 的矩形工具，自带文案原文就是「绘制矩形」）。
/// <para>要钉住的是拖动语义（锚点是按下的那一角、往回拖也要翻正）、只点一下不留零尺寸框、
/// Esc 连元素一起撤，以及<strong>拖的过程中画布就得跟着变</strong>（今天同一族坑踩过两次，见 §五-148）。</para>
/// </summary>
public class RectToolTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static TemplateEditorViewModel Open()
    {
        var template = new LabelTemplate
        {
            Id = "user.rect", Name = "矩形测试", WidthMm = 100, HeightMm = 60, PaddingMm = 2, BorderMm = 0,
        };
        return new TemplateEditorViewModel(template,
            new TemplateStore(Path.Combine(Path.GetTempPath(), "labelgou-rect-" + Guid.NewGuid().ToString("N")[..6])));
    }

    [Fact]
    public void DraggingFromOneCornerDrawsTheBox() => OnSta(() =>
    {
        var vm = Open();
        vm.IsRectTool = true;

        Assert.True(vm.BeginRect(10, 12));
        vm.DragRect(50, 32);
        var element = Assert.Single(vm.Template.Elements);
        Assert.Equal(ElementKind.Rect, element.Kind);
        Assert.Equal((10d, 12d, 40d, 20d), (element.X, element.Y, element.Width, element.Height));
        Assert.True(vm.IsDrawingRect);

        vm.EndRect();
        Assert.False(vm.IsDrawingRect);
        Assert.Equal((40d, 20d), (element.Width, element.Height));
        return true;
    });

    [Fact]
    public void DraggingUpAndLeftFlipsTheAnchorWithoutInvertingTheBox() => OnSta(() =>
    {
        var vm = Open();
        vm.IsRectTool = true;
        vm.BeginRect(60, 40);
        vm.DragRect(20, 15);                                  // 往左上拖

        var element = Assert.Single(vm.Template.Elements);
        Assert.Equal((20d, 15d, 40d, 25d), (element.X, element.Y, element.Width, element.Height));
        return true;
    });

    [Fact]
    public void AClickWithoutDraggingGivesAUsableBoxNotAnInvisibleOne() => OnSta(() =>
    {
        // 只点不拖：不能留一只 0.8mm 的"看不见当没画上"的框，给默认尺寸。
        var vm = Open();
        vm.IsRectTool = true;
        vm.BeginRect(12, 12);
        vm.DragRect(12.4, 12.4);
        vm.EndRect();

        var element = Assert.Single(vm.Template.Elements);
        Assert.Equal((TemplateEditorViewModel.DefaultRectWidthMm, TemplateEditorViewModel.DefaultRectHeightMm),
            (element.Width, element.Height));
        return true;
    });

    [Fact]
    public void TheBoxIsLiveOnTheCanvasWhileDragging() => OnSta(() =>
    {
        // 画布吃的是 SampleLayout（不是模板）：松手才重建的话，拖的时候就是空的。§五-148 同族。
        var vm = Open();
        vm.IsRectTool = true;
        vm.BeginRect(10, 10);
        vm.DragRect(46, 28);

        var box = Assert.Single(vm.SampleLayout.Items.OfType<RectItem>());
        Assert.Equal((36d, 18d), (box.Width, box.Height));
        return true;
    });

    [Fact]
    public void EscapeThrowsAwayTheRectangleInProgress() => OnSta(() =>
    {
        var vm = Open();
        vm.IsRectTool = true;
        vm.BeginRect(20, 20);
        vm.DragRect(40, 40);
        Assert.Single(vm.Template.Elements);

        vm.CancelRect();
        Assert.Empty(vm.Template.Elements);                   // 元素一起撤，不是留一只空框
        Assert.False(vm.IsDrawingRect);
        return true;
    });

    [Fact]
    public void EachRectangleIsOneUndoStep() => OnSta(() =>
    {
        var vm = Open();
        vm.IsRectTool = true;
        vm.BeginRect(10, 10);
        vm.DragRect(40, 30);
        vm.EndRect();
        vm.Undo();
        Assert.Empty(vm.Template.Elements);                   // 画一只框＝一步，别要人按五次撤销
        return true;
    });

    [Fact]
    public void RectFromCornersIsTheSingleSourceOfTheDragMath()
    {
        var (x, y, w, h) = TemplateFactory.RectFromCorners(50, 40, 10, 10);
        Assert.Equal((10d, 10d, 40d, 30d), (x, y, w, h));

        var tiny = TemplateFactory.RectFromCorners(20, 20, 20.1, 20.2);
        Assert.True(tiny.Width >= EditGeometry.MinSideMm && tiny.Height >= EditGeometry.MinSideMm);
    }
}
