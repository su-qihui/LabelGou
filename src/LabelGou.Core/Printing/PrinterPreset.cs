using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelGou.Core.Printing;

/// <summary>
/// 从驱动交回的 DEVMODE 字节里解出来的几件公开事实。
/// <para>
/// 只解公开字段（偏移按 DEVMODEW 布局，与本机 RICOH / Canon 两份真 blob 对过）。
/// 介质类型这类住在驱动私有块里的东西**解不出来**——所以「默认方案」存的是整份 blob，
/// 不是这三项的值清单（只搬那三项会把「标签纸」丢掉，实测过：<c>dmMediaType</c> 纹丝不动）。
/// </para>
/// </summary>
public sealed record DevModeFacts(int PaperSizeId, double? WidthMm, double? LengthMm, int DefaultSource, int MediaType)
{
    /// <summary>DMPAPER_USER：尺寸由 blob 里的宽/长自己说。</summary>
    public const int PaperUser = 256;

    public string PaperText => PaperSizeId switch
    {
        PaperUser when WidthMm is > 0 && LengthMm is > 0 => $"自定义 {WidthMm:0.#}×{LengthMm:0.#}mm",
        PaperUser => "自定义尺寸（没读到宽高）",
        9 => "A4",
        8 => "A3",
        11 => "A5",
        1 => "Letter",
        5 => "Legal",
        0 => "没指定",
        _ => $"纸张编号 {PaperSizeId}",
    };

    /// <summary>
    /// 纸盘只能报到编号：这台驱动不通过 <c>DeviceCapabilities</c> 给出可用的编号↔名字对照
    /// （实测只回 2 个码、5 个名字，数量都对不上），硬翻就成了猜。
    /// </summary>
    public string TrayText => DefaultSource switch
    {
        7 => "自动选择（编号 7）",
        0 => "没指定（编号 0）",
        _ => $"纸盘编号 {DefaultSource}",
    };

    public string Describe() => $"{PaperText} · {TrayText}";

    /// <summary>从一份 DEVMODE 字节里取公开字段；短得不像一份 DEVMODE 时给"读不到"，不抛。</summary>
    public static DevModeFacts Read(byte[] devMode)
    {
        if (devMode.Length < 220) return new DevModeFacts(0, null, null, 0, 0);
        ushort U16(int offset) => (ushort)(devMode[offset] | (devMode[offset + 1] << 8));
        int U32(int offset) => devMode[offset] | (devMode[offset + 1] << 8)
                             | (devMode[offset + 2] << 16) | (devMode[offset + 3] << 24);
        // 偏移按 DEVMODEW：dmPaperSize 78、dmPaperLength 80、dmPaperWidth 82（单位 0.1mm）、
        // dmDefaultSource 88、dmMediaType 196。与本机两份真 blob 对过（Canon 的 7=自动、RICOH 的 4=手送台）。
        return new DevModeFacts(U16(78), U16(82) / 10.0, U16(80) / 10.0, U16(88), U32(196));
    }
}

/// <summary>一份「打印机默认方案」：某台打印机的一份完整驱动设置。</summary>
public sealed record PrinterPreset(
    string Name,
    string PrinterName,
    string Summary,
    string SavedAt,
    [property: JsonPropertyName("devmode")] string DevModeBase64)
{
    public byte[] DevMode => Convert.FromBase64String(DevModeBase64);

    public DevModeFacts Facts => DevModeFacts.Read(DevMode);
}

/// <summary>
/// 默认方案库：<c>%APPDATA%\LabelGou\printer-presets\*.json</c>（根目录走 <see cref="UserPaths"/>）。
/// 与模板库/纸规库同一套规矩：坏文件不静默蒸发，列出来时把「哪份没进来、为什么」一并带回。
/// </summary>
public sealed class PrinterPresetStore
{
    public static string DefaultDirectory => Path.Combine(UserPaths.Root, "printer-presets");

    private readonly string _directory;

    public PrinterPresetStore(string? directory = null) => _directory = directory ?? DefaultDirectory;

    public IReadOnlyList<PrinterPreset> List() => ListWithReport().Presets;

    public PresetListReport ListWithReport()
    {
        var list = new List<PrinterPreset>();
        var skipped = new List<string>();
        if (!System.IO.Directory.Exists(_directory)) return new PresetListReport(list, skipped);
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f))
        {
            var (preset, reason) = TryRead(file);
            if (preset is not null) list.Add(preset);
            else skipped.Add($"{Path.GetFileName(file)}：{reason}");
        }
        return new PresetListReport(list, skipped);
    }

    /// <summary>按名字取（同名取最后存的那份）。</summary>
    public PrinterPreset? GetByName(string? name) => string.IsNullOrWhiteSpace(name)
        ? null
        : List().LastOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    /// <summary>存一份；同名就覆盖（这套方案本来就是"当前驱动设置的快照"，留两份没意义）。</summary>
    public PrinterPreset Save(string name, string printerName, byte[] devMode)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(printerName);
        if (devMode.Length < 220)
            throw new ArgumentException($"这份驱动设置只有 {devMode.Length} 字节，不像一份 DEVMODE。");

        var preset = new PrinterPreset(name, printerName, DevModeFacts.Read(devMode).Describe(),
            DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Convert.ToBase64String(devMode));
        System.IO.Directory.CreateDirectory(_directory);
        var json = JsonSerializer.Serialize(preset, JsonOptions);
        File.WriteAllText(Path.Combine(_directory, MakeFileName(name) + ".json"), json);
        return preset;
    }

    /// <summary>删一份（先备份，与模板库同一做法）；内置没有这一说，所以只认用户存的文件。</summary>
    public bool Delete(string name, out string? reason)
    {
        reason = null;
        var path = Path.Combine(_directory, MakeFileName(name) + ".json");
        if (!File.Exists(path))
        {
            reason = $"没有叫「{name}」的方案文件。";
            return false;
        }
        var backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.Move(path, backup, overwrite: true);
        return true;
    }

    private static (PrinterPreset? Preset, string? Reason) TryRead(string file)
    {
        try
        {
            var preset = JsonSerializer.Deserialize<PrinterPreset>(File.ReadAllText(file), JsonOptions);
            if (preset is null) return (null, "文件是空的");
            if (string.IsNullOrWhiteSpace(preset.Name)) return (null, "缺 name");
            if (string.IsNullOrWhiteSpace(preset.PrinterName)) return (null, "缺打印机名");
            var bytes = Convert.FromBase64String(preset.DevModeBase64);   // 解不出就抛，下面接住
            if (bytes.Length < 220) return (null, $"DEVMODE 只有 {bytes.Length} 字节，不像一份完整设置");
            return (preset, null);
        }
        catch (JsonException)
        {
            return (null, "JSON 语法不对（文件可能被别的程序改过）");
        }
        catch (FormatException)
        {
            return (null, "里面那份 DEVMODE 不是合法的 base64");
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>方案名能带中文与空格，文件名里要换掉——与模板库同一口径。</summary>
    public static string MakeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => bad.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).Trim();
        return safe.Length == 0 ? "未命名" : safe;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>列出来的方案 + 那些没进来的文件与原因。</summary>
public sealed record PresetListReport(IReadOnlyList<PrinterPreset> Presets, IReadOnlyList<string> Skipped);
