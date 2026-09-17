using LabelGou.Core.Editing;
using LabelGou.Core.Impos;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 82 棒①：图片进模板时**框该是图的形状**（用户：「导入图片比例显示不正常」）。
/// <para>渲染端把位图填满框是 CorelDRAW 的语义（框就是你拖的那个东西），所以病根在落框那一步：
/// 「图片」那颗按钮从前不管文件是什么形状一律给 20×12mm，一张 4:3 的截图进来就横着压扁。
/// 这份算式收在 Core 一处（App 的「图片」与「粘贴」两条入口都走它），所以判据也放在 Core。</para>
/// </summary>
public class ImageSizingTests
{
    [Fact]
    public void NaturalSizeComesFromTheFilesOwnDpi()
    {
        // 96 DPI 是"一像素 = 一设备无关单位"：40×20 px → 10.58×5.29 mm
        var (w, h) = ImageSizing.NaturalSizeMm(40, 20, 96, 96);
        Assert.Equal(10.58, w, 2);
        Assert.Equal(5.29, h, 2);

        // 300 DPI 的同一张图按物理尺寸算，只有 96 DPI 的三分之一
        var (w300, _) = ImageSizing.NaturalSizeMm(40, 20, 300, 300);
        Assert.True(w300 < w / 2.5, $"300dpi 的图不该和 96dpi 一样大：{w300:0.##} vs {w:0.##}");
    }

    [Fact]
    public void TheAspectRatioSurvivesWhateverThePaperIs()
    {
        var (w, h) = ImageSizing.PlacementFor(pixelWidth: 800, pixelHeight: 600, dpiX: 96, dpiY: 96,
            availableWidthMm: 130, availableHeightMm: 90);
        Assert.Equal(800 / 600d, w / h, 3);      // 等比缩，绝不各自夹一条边

        // 40×30 px = 10.58×7.94 mm，离"最小可见边 0.8mm"那条保底还远，量的才是真比例。
        // （再小一档就被保底抬起单轴，比例必然变形——那是给看不见的小图兜底，不是这条要测的事。）
        var (nw, nh) = ImageSizing.PlacementFor(40, 30, 96, 96, 130, 90);
        Assert.Equal(4 / 3d, nw / nh, 3);
    }

    /// <summary>小图不许凭空放大：放大只会糊，摆位置有拖角。</summary>
    [Fact]
    public void ASmallImageStaysSmall()
    {
        var (w, h) = ImageSizing.PlacementFor(pixelWidth: 40, pixelHeight: 20, dpiX: 96, dpiY: 96,
            availableWidthMm: 130, availableHeightMm: 90);
        Assert.Equal(10.58, w, 2);
        Assert.Equal(5.29, h, 2);
    }

    /// <summary>一张屏幕截图按原样大是 200 多毫米，任何唛头纸都装不下 → 等比缩到内容区。</summary>
    [Fact]
    public void AnOversizeScreenshotIsScaledDownToFitTheContentArea()
    {
        var (w, h) = ImageSizing.PlacementFor(pixelWidth: 1920, pixelHeight: 1080, dpiX: 96, dpiY: 96,
            availableWidthMm: 130, availableHeightMm: 90);
        Assert.Equal(130, w, 2);                 // 宽说了算（16:9 比内容区更扁）
        Assert.True(w / h > 1.7 && w / h < 1.78, $"比例被改了：{w / h:0.###}");
    }

    /// <summary>坏数据（零像素、没写 DPI、可用区为 0）不许炸，也不许给出零面积的元素——校验器会直接报错。</summary>
    [Fact]
    public void DegenerateInputsLandOnSomethingVisible()
    {
        Assert.Equal(ImageSizing.MinSideMm, ImageSizing.PlacementFor(0, 0, 0, 0, 130, 90).Width, 3);
        var (w, h) = ImageSizing.PlacementFor(100, 50, 0, 0, 0, 0);   // DPI 没写 → 按 96 兜；可用区 0 → 不夹
        Assert.True(w > 0 && h > 0, $"算出零面积元素：{w}×{h}");
        Assert.Equal(2d, w / h, 3);
    }

    [Fact]
    public void SheetSpecStillDefaultsToNoDieLine()
    {
        // 第 82 棒③的分界线刻意不借纸规那格（LabelOutlineMm）：它默认 0，且整段 marks 套在「含裁切线」里
        Assert.Equal(0, new SheetSpec { Id = "s", Name = "n", PaperWidthMm = 297, PaperHeightMm = 210 }.LabelOutlineMm);
    }
}
