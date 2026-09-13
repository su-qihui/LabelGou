using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core.Colors;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 调色盘那一对方块（第 47 棒补刀）。用户要的是"拖"，不是"填数字"，但数字那两档他明确说了留着。
/// <para>
/// 这一层真正要钉住的不是换算对不对（<c>HsvMathTests</c> 管），而是<strong>手感不许骗人</strong>：
/// ① 拖完方块，面板停在哪一档就还在哪一档——不许一拖就把 CMYK 四格换成 RGB 三格；
/// ② 圈拖过方块底边那条黑线再拖回来，颜色该是原来那支（黑色上饱和度没有定义，
///    每次从颜色反推就会把饱和度丢掉，这是本条补刀存在的理由）；
/// ③ 圈与色相带上的位置，得真的指着当前那支墨。
/// </para>
/// </summary>
public class InkPickerTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static (TemplateEditorViewModel Vm, EditableElement Panel, TemplateElement Element) Open()
    {
        var template = new LabelTemplate
        {
            Id = "user.picker", Name = "调色盘测试", WidthMm = 100, HeightMm = 80, PaddingMm = 4, BorderMm = 0,
        };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "MADE IN CHINA", X = 10, Y = 10, Width = 60, Height = 8, FontSizePt = 12,
        });
        var vm = new TemplateEditorViewModel(template,
            new TemplateStore(Path.Combine(Path.GetTempPath(), "labelgou-picker-" + Guid.NewGuid().ToString("N")[..6])));
        vm.SelectedRow = vm.Elements[0];
        var panel = vm.Editing ?? throw new InvalidOperationException("选中行没建起来，面板不成立");
        return (vm, panel, vm.Template.Elements[0]);
    }

    [Fact]
    public void DraggingTheSquareKeepsThePanelOnItsCmykChannel() => OnSta(() =>
    {
        var (_, panel, element) = Open();
        Assert.True(panel.InkUsesCmyk);                                  // 没填过颜色时面板给 CMYK 档（这活是印刷活）

        panel.PickerSaturation = 1;
        panel.PickerValue = 1;                                           // 方块右上角＝当前色相的纯色（红色相）

        Assert.True(panel.InkUsesCmyk, "拖一下方块就把面板弹到 RGB 档，四格墨量当场看不见也改不了");
        Assert.Equal(ColorEntrySpace.Cmyk, element.InkColor!.Entry);
        Assert.Equal((0, 100, 100, 0), (element.InkColor.C, element.InkColor.M, element.InkColor.Y, element.InkColor.K));
        Assert.Equal("#ff0000", element.InkColor.ToHex());               // 折回屏幕色与原值同支
        return true;
    });

    [Fact]
    public void DraggingTheSquareInRgbChannelRecordsTheScreenColour() => OnSta(() =>
    {
        var (_, panel, element) = Open();
        panel.InkUsesRgb = true;

        panel.PickerHueDeg = 240;                                        // 色相带拖到蓝
        panel.PickerSaturation = 1;
        panel.PickerValue = 1;

        Assert.Equal(ColorEntrySpace.Srgb, element.InkColor!.Entry);
        Assert.Equal("#0000ff", element.InkColor.ToHex());
        Assert.False(panel.InkUsesCmyk);
        return true;
    });

    [Fact]
    public void DraggingThroughBlackBacksOutToTheSameInk() => OnSta(() =>
    {
        var (_, panel, element) = Open();
        panel.PickerSaturation = 1;
        panel.PickerValue = 1;
        Assert.Equal("#ff0000", element.InkColor!.ToHex());

        panel.PickerValue = 0;                                           // 拖到方块底边那条黑线上
        Assert.Equal("#000000", element.InkColor.ToHex());

        panel.PickerValue = 1;                                           // 原路拖回去
        Assert.Equal("#ff0000", element.InkColor.ToHex());               // 饱和度还在面板手里，不是拖回来变白的
        Assert.Equal(1, panel.PickerSaturation, 6);
        return true;
    });

    [Fact]
    public void TurningTheHueBarOnBlackChangesNothingUntilThereIsInk() => OnSta(() =>
    {
        var (_, panel, element) = Open();
        panel.ApplyInkPresetCommand.Execute(EditableElement.InkPresets[0]);      // 黑
        Assert.Equal("#000000", element.InkColor!.ToHex());

        panel.PickerHueDeg = 300;
        Assert.Equal("#000000", element.InkColor.ToHex());               // 黑上拧色相不该改颜色，只该记住这一格
        Assert.Equal(300, panel.PickerHueDeg, 3);

        panel.PickerValue = 1;                                           // 先把明度提起来（此时还是灰）
        Assert.Equal("#ffffff", element.InkColor.ToHex());

        panel.PickerSaturation = 1;
        Assert.Equal("#ff00ff", element.InkColor.ToHex());               // 刚才那格品红一直给留着
        return true;
    });

    [Fact]
    public void TheThumbSitsWhereTheCurrentInkIs() => OnSta(() =>
    {
        var (_, panel, _) = Open();
        Assert.Equal(0, panel.PickerThumbX, 3);                          // 没填颜色＝黑＝圈在左下角
        Assert.Equal(EditableElement.PickerSquareH, panel.PickerThumbY, 3);

        panel.ApplyInkPresetCommand.Execute(EditableElement.InkPresets[2]);     // 蓝 C100 M100 Y0 K0
        Assert.Equal(240, panel.PickerHueDeg, 1);
        Assert.Equal(1, panel.PickerSaturation, 6);
        Assert.Equal(1, panel.PickerValue, 6);
        Assert.Equal(EditableElement.PickerSquareW, panel.PickerThumbX, 3);
        Assert.Equal(0, panel.PickerThumbY, 3);
        Assert.Equal(133.33, panel.PickerHueThumbY, 2);                  // 240 度在 200 高的带子上走到 2/3 处
        return true;
    });

    [Fact]
    public void TheSquarePaintEndsOnThePureHue() => OnSta(() =>
    {
        var (_, panel, _) = Open();
        panel.PickerHueDeg = 120;

        var paint = Assert.IsType<LinearGradientBrush>(panel.PickerSvFill);
        Assert.True(paint.IsFrozen, "每次刷新都交一支能改的画刷，等于给界面留了个可写的共享对象");
        Assert.Equal(2, paint.GradientStops.Count);
        Assert.Equal(Colors.White, paint.GradientStops[0].Color);        // 左边＝纸白
        Assert.Equal(Colors.Lime, paint.GradientStops[1].Color);         // 右边＝当前色相的纯色
        return true;
    });

    [Fact]
    public void AWholeDragUndoesAsOneStep() => OnSta(() =>
    {
        var (vm, panel, element) = Open();
        panel.PickerHueDeg = 18;                                         // 一路拖：色相、饱和、明度各写好几次
        panel.PickerSaturation = 0.4;
        panel.PickerValue = 0.9;
        panel.PickerSaturation = 0.72;
        panel.PickerValue = 0.63;
        Assert.NotNull(element.InkColor);

        vm.UndoCommand.Execute(null);
        Assert.Null(vm.Template.Elements[0].InkColor);                   // 一次拖算一步，不该要人按五次撤销
        Assert.Null(vm.Editing!.InkColor);
        Assert.Equal(0, vm.Editing!.PickerSaturation, 6);                // 撤销之后圈也要搬回黑那一角
        Assert.Equal(EditableElement.PickerSquareH, vm.Editing!.PickerThumbY, 3);
        return true;
    });

    [Fact]
    public void TheInkNumbersStillDriveTheSquare() => OnSta(() =>
    {
        // 反方向也要通：手打墨量数字时，圈该跟着搬到那支墨真正的位置（面板上两处说的是同一支笔）。
        var (_, panel, element) = Open();
        panel.InkC = 0;
        panel.InkM = 100;
        panel.InkY = 100;
        panel.InkK = 0;
        Assert.Equal("#ff0000", element.InkColor!.ToHex());

        Assert.Equal(1, panel.PickerSaturation, 6);
        Assert.Equal(1, panel.PickerValue, 6);
        Assert.Equal(0, panel.PickerHueDeg, 3);
        Assert.Equal(EditableElement.PickerSquareW, panel.PickerThumbX, 3);
        Assert.Equal(0, panel.PickerThumbY, 3);
        return true;
    });

    [Fact]
    public void ReloadAnnouncesEveryPickerBinding() => OnSta(() =>
    {
        // RaiseAll 里漏一个名字，界面上就是"数字变了、圈还停在老地方"——只有订阅事件看得见这种骗人。
        // 撤销走的是重建行对象那条路，所以这里验面板自己的刷新口（撤销与换选中行最终都调它）。
        var (_, panel, element) = Open();
        panel.PickerSaturation = 1;
        panel.PickerValue = 1;                                       // 此刻这支墨是红的，圈在方块右上角

        var raised = new List<string?>();
        ((INotifyPropertyChanged)panel).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        element.InkColor = null;                                     // 模型里那支墨没了，面板只能靠这一次刷新自己发现
        panel.Reload();

        foreach (var name in new[]
        {
            nameof(EditableElement.InkColor), nameof(EditableElement.InkSwatch), nameof(EditableElement.InkSummary),
            nameof(EditableElement.PickerHueDeg), nameof(EditableElement.PickerSaturation), nameof(EditableElement.PickerValue),
            nameof(EditableElement.PickerThumbX), nameof(EditableElement.PickerThumbY),
            nameof(EditableElement.PickerHueThumbY), nameof(EditableElement.PickerSvFill),
        })
        {
            Assert.Contains(name, raised);
        }
        Assert.Equal(0, panel.PickerSaturation, 6);                  // 圈也确实搬回了左下角
        Assert.Equal(EditableElement.PickerSquareH, panel.PickerThumbY, 3);
        return true;
    });
}
