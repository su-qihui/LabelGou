using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LabelGou.App.Services.Recognition;

/// <summary>
/// 发给模型的图：按真实扩展名给 MIME，并把长边降到 <see cref="MaxEdgePx"/> 以内。
/// <para>
/// 上一版两件事都没做：data URL 里恒写 <c>image/png</c>（.jpg 的单据也被标成 png，严格的云端会拒），
/// 而手机拍的十几 MB 原图整张 base64 上去，超时与流量都疼。够小的图一律原样发，不做二次压缩
/// ——唛头上的小字经不起再编码一次。
/// </para>
/// </summary>
public static class ImageForModel
{
    /// <summary>送给视觉模型前保留的最长边（像素）。2000px 足够认清单据上的字。</summary>
    public const int MaxEdgePx = 2000;

    /// <summary>JPEG 重编码质量：降采样已经丢了细节，这里再压狠一点字就糊了。</summary>
    private const int JpegQuality = 90;

    /// <summary>读文件并准备好可直接发送的那份图。解码失败就原样发（让云端自己去判断，至少不比原来差）。</summary>
    public static (string Base64, string MimeType) FromFile(string imagePath)
    {
        var bytes = File.ReadAllBytes(imagePath);
        return Encode(bytes, MimeTypeOf(imagePath));
    }

    /// <summary>与 <see cref="FromFile"/> 同一套规矩，但喂的是内存里的字节（测试与截图路径用）。</summary>
    public static (string Base64, string MimeType) Encode(byte[] bytes, string mimeType)
    {
        BitmapSource? decoded;
        try
        {
            decoded = Decode(bytes);
        }
        catch (Exception)
        {
            // 文件不是图（或解码器不支持这个容器）：原样发，至少不比改之前差
            return (Convert.ToBase64String(bytes), mimeType);
        }

        if (decoded is null)
            return (Convert.ToBase64String(bytes), mimeType);

        var longest = Math.Max(decoded.PixelWidth, decoded.PixelHeight);
        if (longest <= MaxEdgePx) return (Convert.ToBase64String(bytes), mimeType);

        var scale = MaxEdgePx / (double)longest;
        var resized = new TransformedBitmap(decoded, new ScaleTransform(scale, scale));
        return (Convert.ToBase64String(EncodeJpeg(resized)), "image/jpeg");
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        // 不戴 DelayCreation：那个选项会把解码推迟到访问 Pixels 时，而上面的 MemoryStream 那时已经 dispose 了
        //（真机不会撞见，测试里拿假字节就撞见了）。OnLoad + 立刻取帧 = 解码在流还活着的时候完成。
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return null;
        var frame = decoder.Frames[0];
        if (frame.CanFreeze) frame.Freeze();     // 后面可能换线程编码，冻结了才能跨线程用
        return frame;
    }

    private static byte[] EncodeJpeg(BitmapSource bitmap)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>扩展名 → MIME。认不出来的按 png 报（与历史行为一致，不至于发不出去）。</summary>
    public static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "image/png",
    };
}
