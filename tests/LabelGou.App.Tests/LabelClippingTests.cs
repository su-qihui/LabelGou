using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.App.Rendering;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 探出标签的墨迹<strong>按"被裁掉"呈现</strong>（第 61 棒，用户 2026-09-14 附截图：
/// 「当出现文字超出去时打印的效果是直接截断只保留自内的效果，在整张纸上也是这样」）。
/// <para>刀模就那么大：越过标签边界的墨迹物理上印不到纸上（在拼版上还可能糊到邻枚或底纸上）。
/// 从前渲染端照版面画满，屏幕上就出现他截图那种"字跨过边线跑到纸外"的样子——
/// 看见的是一串印不出的字，这正是"会印错且看不见"那一族的反面（会看见但印不出）。</para>
/// <para>判据落在 <see cref="LabelRenderer.Draw"/> 这一层：预览、位图、打印、PDF、整版五处都从它走，
/// 所以裁一刀五处同见（§七：五出口一张脸）。SVG 出口另有一条同源判据在 <c>SvgInterchangeTests</c>。</para>
/// </summary>
public class LabelClippingTests
{
    private static void OnSta(Action work)
        => LabelGou.App.Export.StaWorker.RunAsync((_, _) =>
        {
            work();
            return true;
        }, null, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void InkHangingOffTheLabelIsCutAtTheEdge() => OnSta(() =>
    {
        var template = new LabelTemplate { Name = "裁切看样", WidthMm = 60, HeightMm = 40, BorderMm = 0 };
        template.Elements.Add(new TemplateElement
        {
            // 一行超长、不缩也不折的字：右半边必然越过标签边界（他截图那行 "11150588" 就是这个形状）
            // 折行宽度默认 0 = 永不折行（NoWrap 是它的派生只读属性），所以只要不缩字号就一定越界。
            Kind = ElementKind.Text, Text = "1115058811150588", X = 20, Y = 8, Width = 160, Height = 20,
            FontSizePt = 40, ShrinkToFit = false, MaxLines = 1,
        });
        var layout = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1));
        Assert.True(layout.Items.OfType<TextItem>().Single().Width > template.WidthMm,
            "版面里这行字没越过边界：这条测试量不到东西");

        const double scale = 2.0;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            LabelRenderer.Draw(dc, layout, scale, offsetX: 0, offsetY: 0,
                showGuides: false, pixelsPerDip: 1.0, drawBackground: true);
        var w = (int)Math.Ceiling(Mm.ToDiu(120) * scale);       // 画布比标签宽：右半边就是"纸外"
        var h = (int)Math.Ceiling(Mm.ToDiu(60) * scale);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);

        var edge = (int)Math.Ceiling(Mm.ToDiu(template.WidthMm) * scale);
        var bottom = (int)Math.Ceiling(Mm.ToDiu(template.HeightMm) * scale);
        Assert.Equal(0, PaintedOutside(bmp, edge, bottom));      // 纸外一个像素都不该有（他截图那行字跨过边线的样子）
        Assert.True(DarkPixelsInside(bmp, edge, h) > 50, "标签内一个墨点都没有：这行字压根没画出来");
    });

    /// <summary>
    /// 标签右边界与下边界之外、被画到（alpha 非 0）的像素数。
    /// <para>边界外先让 2 像素：白底矩形自己的边在那儿有抗锯齿的半透明过渡，那不是墨（第一次就这么误报了一列 303 个）。</para>
    /// </summary>
    private static int PaintedOutside(RenderTargetBitmap bmp, int edge, int bottom)
    {
        var (w, h) = (bmp.PixelWidth, bmp.PixelHeight);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        var outside = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (x < edge + 2 && y < bottom + 2) continue;
                if (px[(y * w + x) * 4 + 3] != 0) outside++;
            }
        return outside;
    }

    /// <summary>标签框内有多少个明显发暗的像素（字有没有真画上去）。</summary>
    private static int DarkPixelsInside(RenderTargetBitmap bmp, int rightEdge, int h)
    {
        var w = bmp.PixelWidth;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
        var dark = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < rightEdge; x++)
            {
                var i = (y * w + x) * 4;
                if (px[i + 3] > 200 && px[i] < 120 && px[i + 1] < 120 && px[i + 2] < 120) dark++;
            }
        return dark;
    }
}
