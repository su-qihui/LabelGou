using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using LabelGou.Core.Recognition;

namespace LabelGou.App.Services.Recognition;

/// <summary>
/// Windows 内置 OCR（<c>Windows.Media.Ocr</c>）适配器 —— M6 的第一通道（定案 D11）。
/// <para>选它而不是镜像里的 PaddleOCR/RapidOCR，理由是本机实测：零第三方依赖（投影程序集随
/// .NET 的 Windows 支持以 app-local 形式拷贝）、完全离线、几十毫秒一张。
/// 代价也很清楚：<strong>只认它装了识别包的语言</strong>，且符号、小数点、词内空格会错，
/// 所以它产出的文本必须过 <see cref="TextNormalizer"/> 清洗，并且只当"证据池"用，不单独定案。</para>
/// <para>这里出现的 WinRT 类型一律不许进 <c>LabelGou.Core</c>（§七-17：Core 是纯逻辑）。</para>
/// </summary>
public static class WindowsOcrReader
{
    /// <summary>能力探测结果。</summary>
    public sealed record Capability(bool Available, string LanguageList, string? Reason)
    {
        /// <summary>给人看的一句话，界面上直接显示，不要转成"未知错误"。</summary>
        public string Describe() => Available
            ? $"系统 OCR 可用（识别包：{LanguageList}）"
            : $"系统 OCR 不可用：{Reason}";
    }

    /// <summary>
    /// 这台电脑到底能不能用 OCR。<b>必须在真正识别之前问一次</b>，
    /// 否则用户只会看到一个不懂的英文异常。
    /// </summary>
    public static Capability Probe()
    {
        try
        {
            var langs = OcrEngine.AvailableRecognizerLanguages
                .Select(TagOf)
                .Where(t => t.Length > 0)
                .ToList();

            return langs.Count == 0
                ? new Capability(false, string.Empty,
                    "这台电脑没装中文「光学字符识别」可选功能（设置→应用→可选功能→添加功能，搜「光学字符识别」）")
                : new Capability(true, string.Join(", ", langs), null);
        }
        catch (Exception ex)
        {
            return new Capability(false, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// 认一张图片。任何失败都变成 <see cref="RecognizedText.Warnings"/> 里的一句话，
    /// 不抛异常出来——打印店操作员看不懂堆栈。
    /// </summary>
    public static async Task<RecognizedText> RecognizeFileAsync(
        string path,
        string? preferredLanguage,
        CancellationToken cancel = default)
    {
        var text = new RecognizedText { Channel = TextChannel.Ocr, SourceName = Path.GetFileName(path) };

        var capability = Probe();
        if (!capability.Available)
        {
            text.Warnings.Add(capability.Reason ?? "系统 OCR 不可用。");
            return text;
        }

        OcrEngine? engine = null;
        var tag = string.IsNullOrWhiteSpace(preferredLanguage) ? capability.LanguageList.Split(',')[0].Trim() : preferredLanguage!.Trim();
        try
        {
            engine = OcrEngine.TryCreateFromLanguage(new Language(tag));
        }
        catch (Exception ex)
        {
            text.Warnings.Add($"创建识别引擎失败（{tag}）：{ex.Message}");
        }

        if (engine is null)
        {
            // 用户指定的语言没包，退到系统里第一个能用的，而不是一口回绝。
            var fallback = capability.LanguageList.Split(',')[0].Trim();
            try
            {
                engine = OcrEngine.TryCreateFromLanguage(new Language(fallback));
            }
            catch (Exception ex)
            {
                text.Warnings.Add($"退回到 {fallback} 也失败了：{ex.Message}");
            }

            if (engine is null)
            {
                text.Warnings.Add($"这台电脑没有「{tag}」识别包，也找不到可用的替代语言。");
                return text;
            }

            text.Warnings.Add($"没有 {tag} 的识别包，已改用 {fallback} 识别。");
        }

        SoftwareBitmap bitmap;
        try
        {
            bitmap = LoadAsSoftwareBitmap(path, text);
        }
        catch (Exception ex)
        {
            text.Warnings.Add($"这张图读不进来：{ex.Message}");
            return text;
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancel);
            sw.Stop();

            foreach (var line in result.Lines)
            {
                // OcrLine 本身没有框，只有 OcrWord.BoundingRect（不是 BoundingRectangle，投影里实际名字已反射取证）。
                // 一行的包围盒取全部词的并集，核对窗口用它画框、点一下定位原图。
                var (x, y, w, h) = BoundsOf(line);
                text.AddLine(line.Text, x, y, w, h);
            }

            if (text.IsEmpty)
                text.Warnings.Add($"识别完了一句话也没有（{sw.ElapsedMilliseconds} ms）。图太糊、字太小或斜着拍，都会这样。");
            else
                text.Warnings.Add($"本地 OCR：{text.LineCount} 行，用时 {sw.ElapsedMilliseconds} ms。");
        }
        catch (OperationCanceledException)
        {
            text.Warnings.Add("识别已取消。");
        }
        catch (Exception ex)
        {
            text.Warnings.Add($"识别失败：{ex.GetType().Name} {ex.Message}");
        }

        return text;
    }

    /// <summary>一行内全部词的包围盒（像素）。没词的返回全 0，不抛。</summary>
    private static (int X, int Y, int W, int H) BoundsOf(OcrLine line)
    {
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        foreach (var word in line.Words)
        {
            var box = word.BoundingRect;
            left = Math.Min(left, (int)Math.Floor(box.X));
            top = Math.Min(top, (int)Math.Floor(box.Y));
            right = Math.Max(right, (int)Math.Ceiling(box.X + box.Width));
            bottom = Math.Max(bottom, (int)Math.Ceiling(box.Y + box.Height));
        }

        if (left > right || top > bottom) return (0, 0, 0, 0);
        return (left, top, right - left, bottom - top);
    }

    /// <summary>
    /// WPF 解码 → BGRA 缓冲 → WinRT 位图。超过引擎上限时等比缩小并留话说明。
    /// <para>刻意不用 <c>System.Drawing</c>：那要额外引包，且与 WPF 是两套像素口径。</para>
    /// </summary>
    private static SoftwareBitmap LoadAsSoftwareBitmap(string path, RecognizedText text)
    {
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(   // 全限定：WinRT 那边也有个 BitmapFrame，CS0104
            new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        if (width == 0 || height == 0) throw new InvalidDataException("画面尺寸为 0");

        var longest = Math.Max(width, height);
        var maxDimension = (int)OcrEngine.MaxImageDimension;    // 静态成员，不是实例属性
        if (maxDimension > 0 && longest > maxDimension)
        {
            var scale = (double)maxDimension / longest;
            converted = new FormatConvertedBitmap(
                new TransformedBitmap(converted, new ScaleTransform(scale, scale)), PixelFormats.Pbgra32, null, 0);
            width = converted.PixelWidth;
            height = converted.PixelHeight;
            text.Warnings.Add($"原图 {longest} 像素超过识别引擎上限 {maxDimension}，已缩到 {Math.Max(width, height)} 像素，细节可能丢失。");
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        return SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>
    /// 取语言标签。<c>Windows.Globalization.Language.Tag</c> 在投影里编译期拿不到（实测 CS1061），
    /// 但运行时确实有这个属性，所以用反射读一次。
    /// </summary>
    private static string TagOf(Language language)
    {
        var type = language.GetType();
        var value = type.GetProperty("Tag")?.GetValue(language) ?? type.GetProperty("LanguageTag")?.GetValue(language);
        return value as string ?? language.ToString() ?? string.Empty;
    }
}
