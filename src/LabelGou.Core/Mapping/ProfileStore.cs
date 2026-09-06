using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelGou.Core.Mapping;

/// <summary>
/// 映射方案的本地持久化（一个方案一个 JSON 文件）。
/// <para>
/// 选 JSON 文件而不是 SQLite：M1 存的是"模板/映射/配置"这类小体积、要人工可读可改的数据，
/// 文件形式便于打印店之间拷来拷去复用，也为将来开源后用户自改留口子。
/// 数据库等到 M2 需要批量任务/历史记录时再评估。
/// </para>
/// </summary>
public sealed class ProfileStore
{
    /// <summary>默认存放目录：%APPDATA%\LabelGou\mapping-profiles。</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LabelGou",
        "mapping-profiles");

    private readonly string _directory;

    public ProfileStore(string? directory = null)
    {
        _directory = directory ?? DefaultDirectory;
    }

    public string Directory => _directory;

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>列出全部方案（按更新时间倒序）。</summary>
    public IReadOnlyList<ProfileSummary> ListAll()
    {
        if (!System.IO.Directory.Exists(_directory)) return Array.Empty<ProfileSummary>();

        var result = new List<ProfileSummary>();
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
        {
            var profile = TryLoadFile(file);
            if (profile is null) continue;
            result.Add(new ProfileSummary(
                Path.GetFileName(file),
                profile.Name,
                profile.Note,
                profile.BoundCount,
                profile.UpdatedAt));
        }
        return result.OrderByDescending(p => p.UpdatedAt).ToList();
    }

    public MappingProfile? Load(string fileName)
        => TryLoadFile(Path.Combine(_directory, fileName));

    public MappingProfile? LoadBySignature(string headerSignature)
    {
        foreach (var summary in ListAll())
        {
            var profile = Load(summary.FileName);
            if (profile?.HeaderSignature == headerSignature) return profile;
        }
        return null;
    }

    /// <summary>
    /// 保存方案。同名会追加短序号避免互相覆盖；返回文件名。
    /// </summary>
    public string Save(MappingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        System.IO.Directory.CreateDirectory(_directory);
        profile.UpdatedAt = DateTime.Now;

        var baseName = Sanitize(string.IsNullOrWhiteSpace(profile.Name) ? "方案" : profile.Name.Trim());
        var fileName = baseName + ".json";
        var suffix = 2;
        while (File.Exists(Path.Combine(_directory, fileName)))
        {
            var existing = TryLoadFile(Path.Combine(_directory, fileName));
            // 覆盖同签名（同一套表头）的方案，不无限堆文件
            if (existing is not null && existing.HeaderSignature == profile.HeaderSignature
                && string.Equals(existing.Name, profile.Name, StringComparison.Ordinal))
            {
                break;
            }
            fileName = $"{baseName}-{suffix++}.json";
        }

        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
        return fileName;
    }

    public bool Delete(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private MappingProfile? TryLoadFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<MappingProfile>(json, JsonOptions);
        }
        catch (Exception)
        {
            // 单个坏文件不该让整个列表打不开；界面会显示"有 N 个方案读取失败"由调用方统计
            return null;
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsWhiteSpace(ch)) { sb.Append('_'); continue; }
            if (invalid.Contains(ch)) continue;
            sb.Append(ch);
        }
        var result = sb.ToString().Trim('_');
        return result.Length == 0 ? "方案" : (result.Length > 60 ? result[..60] : result);
    }

    /// <summary>列表展示用的摘要。</summary>
    public sealed record ProfileSummary(string FileName, string Name, string Note, int BoundCount, DateTime UpdatedAt);
}
