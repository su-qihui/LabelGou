using LabelGou.Core.Colors;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 调色盘底下那层换算（第 47 棒补刀）。
/// <para>
/// HSV 在这个项目里只有一个用途：把"再亮一点、偏品红一点"这种手感翻译成 RGB。
/// 所以测试要钉住的是<strong>手感不会骗人</strong>——六个纯色落在该落的位置、
/// 拖过黑色那一角再拖回来颜色能原样回来（<see cref="FromRgb"/> 对无彩交回 NaN 而不是编一个 0），
/// 以及鼠标越界（比例算出负数或大于 1）只会夹到边上、不会算出一个没定义的颜色。
/// </para>
/// </summary>
public class HsvMathTests
{
    [Theory]
    [InlineData(0.0, 255, 0, 0)]        // 红
    [InlineData(60.0, 255, 255, 0)]     // 黄
    [InlineData(120.0, 0, 255, 0)]      // 绿
    [InlineData(180.0, 0, 255, 255)]    // 青
    [InlineData(240.0, 0, 0, 255)]      // 蓝
    [InlineData(300.0, 255, 0, 255)]    // 品红
    [InlineData(360.0, 255, 0, 0)]      // 转一圈回到红：色相带两端同色，拖到最底下不该变紫
    public void FullSaturationHitsTheSixPrimaries(double hue, byte r, byte g, byte b)
        => Assert.Equal((r, g, b), HsvMath.ToRgb(hue, 1, 1));

    [Fact]
    public void ZeroValueIsBlackWhateverTheHue()
    {
        // 方块底边那一条整排都是黑的：色相在那儿没有意义，但绝不能算出个灰来
        Assert.Equal((0, 0, 0), HsvMath.ToRgb(0, 1, 0));
        Assert.Equal((0, 0, 0), HsvMath.ToRgb(210, 0.7, 0));
    }

    [Fact]
    public void ZeroSaturationIsGreyWhateverTheHue()
    {
        Assert.Equal((128, 128, 128), HsvMath.ToRgb(210, 0, 0.5));       // 127.5 四舍五入离零
        Assert.Equal((255, 255, 255), HsvMath.ToRgb(30, 0, 1));          // 方块左上角＝纸白
    }

    [Fact]
    public void OutOfRangeRatiosClampInsteadOfThrowing()
    {
        // 这三个数来自鼠标位置，拖出边框就是常态：越界只能夹到边上，不该抛也不该算出负色
        Assert.Equal(HsvMath.ToRgb(0, 1, 1), HsvMath.ToRgb(0, 1.8, 2.4));
        Assert.Equal(HsvMath.ToRgb(0, 0, 0), HsvMath.ToRgb(0, -3, -3));
    }

    [Theory]
    [InlineData(-60.0, 300.0)]
    [InlineData(420.0, 60.0)]
    [InlineData(-0.0001, 359.9999)]
    public void HueWrapsAroundTheWheel(double entered, double equivalent)
    {
        Assert.Equal(HsvMath.ToRgb(equivalent, 0.8, 0.6), HsvMath.ToRgb(entered, 0.8, 0.6));
        Assert.Equal(HsvMath.NormalizeHue(equivalent), HsvMath.NormalizeHue(entered), 3);
    }

    [Fact]
    public void NormalizeHueKeepsItOnTheDial()
    {
        Assert.Equal(270, HsvMath.NormalizeHue(-90), 3);
        Assert.Equal(0, HsvMath.NormalizeHue(720), 3);
        Assert.Equal(0, HsvMath.NormalizeHue(double.NaN), 3);            // 传进来个 NaN 只能当"没选过"收
    }

    [Theory]
    [InlineData(255, 0, 0, 0.0)]
    [InlineData(255, 255, 0, 60.0)]
    [InlineData(0, 255, 0, 120.0)]
    [InlineData(0, 255, 255, 180.0)]
    [InlineData(0, 0, 255, 240.0)]
    [InlineData(255, 0, 255, 300.0)]
    [InlineData(255, 0, 128, 329.9)]    // 绿分量小于蓝：色相在 300~360 之间，别算成负数
    [InlineData(0, 128, 255, 209.9)]
    public void ReadBackTheHueTheWheelWouldShow(int r, int g, int b, double expectedHue)
    {
        var (hue, _, _) = HsvMath.FromRgb(r, g, b);
        Assert.False(double.IsNaN(hue));
        Assert.Equal(expectedHue, hue, 1);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(128, 128, 128)]
    [InlineData(255, 255, 255)]
    public void GreyHasNoHueAndSaysSo(int r, int g, int b)
    {
        var (hue, sat, val) = HsvMath.FromRgb(r, g, b);
        Assert.True(double.IsNaN(hue), "无彩上色相没有定义：交回 0 会让面板把用户刚选的品红悄悄换成红");
        Assert.Equal(0, sat, 6);
        Assert.Equal(r / 255.0, val, 6);
    }

    [Fact]
    public void SaturationAndValueFollowTheirOwnDefinitions()
    {
        var (hue, sat, val) = HsvMath.FromRgb(128, 64, 64);
        Assert.Equal(0, hue, 3);                                         // g 与 b 相等 → 落在 0 度上，这是一支暗红而不是绿
        Assert.Equal(0.5, sat, 6);                                       // (max-min)/max = 64/128
        Assert.Equal(128 / 255.0, val, 6);
    }

    [Fact]
    public void RoundTripKeepsEveryChannelExact()
    {
        // 8 位 RGB ↔ HSV 往返必须逐位相等：面板上拖一下圈、颜色就悄悄偏一格的观感从这里来
        var cases = new[]
        {
            (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (0, 255, 255), (255, 0, 255),
            (153, 90, 216), (31, 26, 23), (200, 120, 40), (10, 200, 190), (1, 1, 2), (250, 12, 130),
            (77, 0, 129), (255, 254, 253), (3, 3, 4),
        };
        foreach (var (r, g, b) in cases)
        {
            var (hue, sat, val) = HsvMath.FromRgb(r, g, b);
            var back = HsvMath.ToRgb(double.IsNaN(hue) ? 0 : hue, sat, val);
            Assert.Equal((r, g, b), back);
        }
    }

    [Fact]
    public void DraggingThroughBlackBacksOutToTheSameColour()
    {
        // 这条是补刀的全部理由：圈从纯品红拖到黑线、再拖回原处，颜色该是原来那支。
        // （面板那侧记住饱和度才做得到，见 EditableElement.PickerSaturation 的注释；这里只验换算本身不丢信息。）
        var (hue, sat, val) = HsvMath.FromRgb(255, 0, 255);
        var atBlack = HsvMath.ToRgb(hue, sat, 0);
        Assert.Equal((0, 0, 0), atBlack);
        Assert.Equal((255, 0, 255), HsvMath.ToRgb(hue, sat, val));
    }
}
