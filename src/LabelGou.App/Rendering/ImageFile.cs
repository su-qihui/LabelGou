using System;

namespace LabelGou.App.Rendering;

/// <summary>
/// 读一张位图的<strong>尺寸与 DPI</strong>（不像素解码）——图片进模板时算"原样大"要用（第 82 棒①）。
/// <para>为什么要单独一个类：渲染端要的是画得出来的位图对象，而落框那一步只想知道"这文件本来是多大"。
/// <c>DelayCreation</c> 让 WPF 只读头信息就把
/// <c>PixelWidth/PixelHeight/DpiX/DpiY</c> 交出来，不必为量尺寸先解一张 3MB 的图。</para>
/// </summary>
public static class ImageFile
{
    /// <summary>读不出来时返回全 0——交给 <c>ImageSizing</c> 兜一只看得见的最小框，比"点了没反应"强。</summary>
    public static (int PixelWidth, int PixelHeight, double DpiX, double DpiY) ReadDimensions(string path)
    {
        try
        {
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri(path, UriKind.Absolute),
                System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight, frame.DpiX, frame.DpiY);
        }
        catch (Exception)
        {
            return (0, 0, 0, 0);
        }
    }
}
