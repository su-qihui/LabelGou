namespace LabelGou.Core.Editing;

/// <summary>
/// 一张图片进模板时该占多大（毫米）——<strong>全项目唯一一份算式</strong>（第 82 棒①）。
/// <para>用户报的是「导入图片比例显示不正常」：渲染端把位图填满元素框是 CorelDRAW 的语义
/// （框就是你拖的那个东西，见 <c>ImageItem</c> 的注释），病根在<strong>落框那一步</strong>——
/// 「图片」那颗按钮从前不管文件是什么形状一律给 20×12mm，一张 4:3 的截图进来就横着压扁。
/// 第 81 棒加「粘贴」时按文件自己的像素与 DPI 算了一次，却没回头改那颗按钮，
/// 于是同一件事有两份代码、只修了一份（§五-62 那一族）。两条入口现在都走这里。</para>
/// <para>两条口径：<strong>不凭空放大</strong>（小图按原样大进来，放大只会糊，要大有拖角）；
/// <strong>等比缩</strong>（一张屏幕截图原样大是 200 多毫米，任何唛头纸都装不下，只能整块缩，
/// 绝不各夹一条边——一夹比例就又不对了）。</para>
/// </summary>
public static class ImageSizing
{
    /// <summary>装不下时的最小边长（毫米）：零面积元素既画不出来也过不了校验器。</summary>
    public const double MinSideMm = EditGeometry.MinSideMm;

    /// <summary>没写 DPI 的位图按 96 算（Windows 的缺省，也是"一像素＝一设备无关单位"那一档）。</summary>
    public const double FallbackDpi = 96.0;

    /// <summary>这张图"原样大"是多少毫米：像素 ÷ 它自己的 DPI × 25.4。</summary>
    public static (double Width, double Height) NaturalSizeMm(int pixelWidth, int pixelHeight, double dpiX, double dpiY)
        => (Math.Max(0, pixelWidth) * 25.4 / (dpiX > 0 ? dpiX : FallbackDpi),
            Math.Max(0, pixelHeight) * 25.4 / (dpiY > 0 ? dpiY : FallbackDpi));

    /// <summary>
    /// 落框尺寸：原样大，装不进 <paramref name="availableWidthMm"/> × <paramref name="availableHeightMm"/> 才等比缩。
    /// <para>可用区给 0 或负数＝不夹（调用方没有"内容区"这个概念时，比如量一张图的原始尺寸）。</para>
    /// </summary>
    public static (double Width, double Height) PlacementFor(int pixelWidth, int pixelHeight, double dpiX, double dpiY,
        double availableWidthMm, double availableHeightMm)
    {
        var (w, h) = NaturalSizeMm(pixelWidth, pixelHeight, dpiX, dpiY);
        if (w <= 0 || h <= 0) return (MinSideMm, MinSideMm);        // 零像素/坏文件：给一只看得见的最小框，别塞个 0 进模板

        var fit = 1d;
        if (availableWidthMm > 0) fit = Math.Min(fit, availableWidthMm / w);
        if (availableHeightMm > 0) fit = Math.Min(fit, availableHeightMm / h);
        return (Math.Max(MinSideMm, w * fit), Math.Max(MinSideMm, h * fit));
    }
}
