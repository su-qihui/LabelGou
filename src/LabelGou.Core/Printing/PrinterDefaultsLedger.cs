using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelGou.Core.Printing;

/// <summary>
/// 一台打印机在被打扰之前的本用户默认（<paramref name="OriginalBase64"/> 为 null = 原本**没有**这一份值）。
/// </summary>
/// <param name="PrinterName">打印机名（恢复时要按这个名字写回去）。</param>
/// <param name="OriginalBase64">那份原始 DEVMODE 的字节；null = 这台打印机原本没有本用户默认。</param>
/// <param name="TakenAt">什么时候记下的（人话格式，出问题时好对时间线）。</param>
public sealed record PrinterDefaultsBackup(string PrinterName, string? OriginalBase64, string TakenAt)
{
    /// <summary>原本那份字节（null = 原本没有这一份值，恢复＝删掉）。</summary>
    [JsonIgnore]
    public byte[]? Original => OriginalBase64 is null ? null : Convert.FromBase64String(OriginalBase64);
}

/// <summary>
/// <strong>退出时要把打印机默认恢复回原样，先得记住"原样是什么"</strong>——这本账（第 84 棒）。
/// <para>为什么要它（用户口径）：⑤ 步那三格与「打印首选项…」改的是
/// <c>HKCU\Printers\DevModePerUser</c>，也就是<strong>这台打印机在本机的用户默认</strong>——
/// 第 76/77 棒是刻意选它的（托管打印票写不进去，见 §五-155/159）。代价就是用户今天说的：
/// 「对系统进行了更改，导致在其他软件调用打印机也用了软件的配置」。所以软件退出时要把原值写回去。</para>
/// <para>账必须<strong>落在磁盘上</strong>：进程被强杀、蓝屏、断电时 <c>OnExit</c> 根本不会跑，
/// 店里那台机器就会一直留着 LabelGou 的配置——那比"没做这个功能"更糟，因为它悄悄留着。
/// 下次启动先照这本账恢复，再把账清掉。</para>
/// <para>一台打印机<strong>只记第一次</strong>：第二次写之前那份已经是被我们改过的值，拿它当"原样"就白恢复了。</para>
/// </summary>
public sealed class PrinterDefaultsLedger
{
    /// <summary>账本目录（一台一个文件，与方案库同一做法：坏一个不牵连别的）。</summary>
    public static string DefaultDirectory => Path.Combine(UserPaths.Root, "printer-defaults");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;

    /// <summary>单测递自己的临时目录，别往真用户的 <c>%APPDATA%</c> 里写东西。</summary>
    public PrinterDefaultsLedger(string? directory = null) => _directory = directory ?? DefaultDirectory;

    /// <summary>
    /// 记一笔"这台打印机原本是什么"。已经记过就<strong>不覆盖</strong>并返回 false（调用方据此知道：
    /// 这台已经在账上了，恢复时会一起处理）。
    /// </summary>
    public bool NoteOriginal(string printerName, byte[]? original)
    {
        Net6Compat.ThrowIfNullOrEmpty(printerName);
        var path = PathOf(printerName);
        if (File.Exists(path)) return false;

        Directory.CreateDirectory(_directory);
        var record = new PrinterDefaultsBackup(printerName, original is null ? null : Convert.ToBase64String(original),
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
        return true;
    }

    /// <summary>账上挂着哪几台（按文件名排序，稳定）。坏文件不静默蒸发——它会让这台永远恢复不了，必须看得见。</summary>
    public IReadOnlyList<PrinterDefaultsBackup> Pending()
    {
        var list = new List<PrinterDefaultsBackup>();
        if (!Directory.Exists(_directory)) return list;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var (backup, _) = TryRead(file);
            if (backup is not null) list.Add(backup);
        }
        return list;
    }

    /// <summary>账上读不回来的文件与原因（恢复完还在，说明那份文件坏了——要告诉用户是哪台没恢复）。</summary>
    public IReadOnlyList<string> Unreadable()
    {
        var bad = new List<string>();
        if (!Directory.Exists(_directory)) return bad;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var (backup, reason) = TryRead(file);
            if (backup is null) bad.Add($"{file}：{reason}");
        }
        return bad;
    }

    /// <summary>这台已经恢复回去了，撤账。</summary>
    public void Clear(string printerName)
    {
        var path = PathOf(printerName);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathOf(string printerName)
        => Path.Combine(_directory, MakeFileName(printerName) + "-" + StableHash(printerName) + ".json");

    /// <summary>
    /// 打印机名换非法字符后仍可能撞成同一个文件（<c>A/B</c> 与 <c>A_B</c>），撞了就有一台悄悄没被恢复。
    /// <para>所以文件名后挂一段稳定哈希。<strong>必须自己算</strong>：<c>string.GetHashCode</c> 对字符串是按进程
    /// 随机化的，拿它当文件名会让下次启动找不到上次那本账——而"下次启动补还"正是这本账存在的理由。</para>
    /// </summary>
    private static string StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in value) hash = (hash ^ c) * 16777619;
            return hash.ToString("x8");
        }
    }

    private static (PrinterDefaultsBackup? Backup, string? Reason) TryRead(string file)
    {
        try
        {
            var read = JsonSerializer.Deserialize<PrinterDefaultsBackup>(File.ReadAllText(file), JsonOptions);
            if (read is null) return (null, "文件是空的");
            if (string.IsNullOrWhiteSpace(read.PrinterName)) return (null, "缺打印机名");
            if (read.OriginalBase64 is not null) _ = Convert.FromBase64String(read.OriginalBase64);   // 解不出就抛，下面接住
            return (read, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>打印机名里有 <c>()[]#/</c> 空格和中文——换掉非法字符，与模板库、方案库同一做法（全项目只这一处算名字）。</summary>
    public static string MakeFileName(string printerName)
    {
        var trimmed = printerName.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) trimmed = trimmed.Replace(c, '_');
        return string.IsNullOrWhiteSpace(trimmed) ? "printer" : trimmed;
    }
}
