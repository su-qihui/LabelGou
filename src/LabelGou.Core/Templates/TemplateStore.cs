using System.Text.Json;
using LabelGou.Core.Mapping;

namespace LabelGou.Core.Templates;

/// <summary>
/// 模板库：内置模板（随程序走，只读）+ 用户模板（JSON 文件，%APPDATA%\LabelGou\templates）。
/// <para>
/// A 类（内置）在 M1 交付；B 类（拖拽自定义）在 M4 复用同一个存储；
/// M7 的 AI 生成模板也落到这里——但<strong>必须先过 <see cref="TemplateValidator"/> 的 Error 检查</strong>，
/// 校验不过一律拒绝入库，这是"AI 出方案、引擎保精度"的落地点。
/// </para>
/// </summary>
public sealed class TemplateStore
{
    /// <summary>
    /// 用户模板目录（图片等相对资源也以它为基准）。根目录走 <see cref="UserPaths"/> 这个单一来源，
    /// 单测才能把它换到临时目录而不是偷读用户真机上的模板库（§五-48 同一条路）。
    /// </summary>
    public static string Directory => Path.Combine(UserPaths.Root, "templates");

    private readonly string _directory;

    public TemplateStore(string? directory = null)
    {
        _directory = directory ?? Directory;
    }

    public string UserDirectory => _directory;

    /// <summary>全部可用模板：内置在前，用户模板按名称排序在后。</summary>
    public IReadOnlyList<LabelTemplate> ListAll()
    {
        var list = new List<LabelTemplate>(BuiltInTemplates.All());
        if (System.IO.Directory.Exists(_directory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f))
            {
                var template = TryRead(file);
                if (template is not null) list.Add(template);
            }
        }
        return list;
    }

    public LabelTemplate? GetById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var builtIn = BuiltInTemplates.GetById(id);
        if (builtIn is not null) return builtIn;
        return ListAll().FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
    }

    /// <summary>
    /// 保存用户模板。<strong>校验有 Error 时直接拒绝</strong>并返回问题清单。
    /// </summary>
    public (bool Saved, string? FileName, IReadOnlyList<TemplateIssue> Issues) Save(LabelTemplate template, bool overwrite = true)
    {
        var issues = TemplateValidator.Validate(template);
        if (issues.HasError()) return (false, null, issues);
        if (template.BuiltIn)
        {
            return (false, null, new[]
            {
                new TemplateIssue(IssueLevel.Error, "内置模板不可覆盖，请先“另存为用户模板”。"),
            });
        }

        System.IO.Directory.CreateDirectory(_directory);
        var fileName = MakeFileName(template);
        var path = Path.Combine(_directory, fileName);
        if (!overwrite && File.Exists(path))
        {
            return (false, null, new[] { new TemplateIssue(IssueLevel.Error, $"已存在同名模板文件 {fileName}，请换个名字。") });
        }

        File.WriteAllText(path, JsonSerializer.Serialize(template, ProfileStore.JsonOptions));
        return (true, fileName, issues);
    }

    /// <summary>
    /// 找出某个用户模板当前存在磁盘上的文件（重命名后会留下旧文件，编辑器保存时需要拿它删陈旧副本）。
    /// 内置模板不在磁盘上，永远返回 null。
    /// </summary>
    public string? FindFileFor(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (!System.IO.Directory.Exists(_directory)) return null;
        return System.IO.Directory.EnumerateFiles(_directory, "*.json")
            .FirstOrDefault(f =>
            {
                var t = TryRead(f);
                return t is not null && string.Equals(t.Id, id, StringComparison.Ordinal);
            });
    }

    public bool Delete(string id)
    {
        if (BuiltInTemplates.GetById(id) is not null) return false;   // 内置不可删
        var file = FindFileFor(id);
        if (file is null) return false;
        File.Delete(file);
        return true;
    }

    /// <summary>
    /// 导出为独立 JSON 文本（拷到另一台机器导入用）。<strong>序列化口径与库内一致</strong>：
    /// <c>JsonOptions</c> 是程序集内部的，不能把“自己再 new 一个 JsonSerializerOptions”的诱惑留给 UI。
    /// </summary>
    public static string ToJson(LabelTemplate template)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        return JsonSerializer.Serialize(template, ProfileStore.JsonOptions);
    }

    public void ExportFile(LabelTemplate template, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导出路径不能为空。", nameof(path));
        File.WriteAllText(path, ToJson(template));
    }

    /// <summary>模板资源子目录名（图与矢量底图都存这儿，与模板 JSON 同根，整目录拷走就能换机器）。</summary>
    public const string AssetFolderName = "assets";

    /// <summary>资源目录绝对路径（不存在时由 <see cref="SaveAsset(string,string)"/> 负责创建）。</summary>
    public string AssetDirectory => Path.Combine(_directory, AssetFolderName);

    /// <summary>
    /// 把一份文本资产（SVG 底图）落进 <c>assets\</c>，返回<strong>可直接填进 <c>ImagePath</c> 的相对路径</strong>。
    /// <para>同名直覆：同一份底稿反复导入时不该留一堆垃圾文件。</para>
    /// </summary>
    public string SaveAsset(string fileName, string content)
    {
        System.IO.Directory.CreateDirectory(AssetDirectory);
        var path = Path.Combine(AssetDirectory, SafeAssetName(fileName));
        File.WriteAllText(path, content);
        return Path.Combine(AssetFolderName, Path.GetFileName(path));
    }

    /// <summary>二进制资产（从 .cdr 抠出来的缩略图、用户选的 Logo）。</summary>
    public string SaveAsset(string fileName, byte[] content)
    {
        System.IO.Directory.CreateDirectory(AssetDirectory);
        var path = Path.Combine(AssetDirectory, SafeAssetName(fileName));
        File.WriteAllBytes(path, content);
        return Path.Combine(AssetFolderName, Path.GetFileName(path));
    }

    /// <summary>按相对路径删一个资源（重导入时清旧的底图）。删不到不当错，宁可留一个孤儿文件也不拦用户。</summary>
    public void DeleteAsset(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return;
        try
        {
            var full = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(_directory, relativePath);
            if (File.Exists(full)) File.Delete(full);
        }
        catch (Exception) { /* 只影响磁盘占用，不影响正确性 */ }
    }

    /// <summary>把名字洗成能当文件用的样子（中文保留，因为模板名本来就是中文）。</summary>
    public static string SafeAssetName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((raw ?? string.Empty).Where(ch => !invalid.Contains(ch)).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length == 0) cleaned = "asset";
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }

    /// <summary>读取单个模板文件（导入/迁移用），同样带校验。</summary>
    public (LabelTemplate? Template, IReadOnlyList<TemplateIssue> Issues) ReadFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var template = JsonSerializer.Deserialize<LabelTemplate>(json, ProfileStore.JsonOptions);
            if (template is null)
                return (null, new[] { new TemplateIssue(IssueLevel.Error, "文件内容不是有效的模板。") });

            template.BuiltIn = false;
            var issues = TemplateValidator.Validate(template);
            return issues.HasError() ? (null, issues) : (template, issues);
        }
        catch (JsonException ex)
        {
            return (null, new[] { new TemplateIssue(IssueLevel.Error, $"JSON 解析失败：{ex.Message}") });
        }
    }

    private LabelTemplate? TryRead(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var template = JsonSerializer.Deserialize<LabelTemplate>(json, ProfileStore.JsonOptions);
            if (template is null) return null;
            template.BuiltIn = false;
            return template;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string MakeFileName(LabelTemplate template)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var raw = new string((template.Name ?? "模板").Where(ch => !invalid.Contains(ch) && !char.IsWhiteSpace(ch)).ToArray());
        if (raw.Length == 0) raw = "模板";
        if (raw.Length > 50) raw = raw[..50];
        var idTail = new string(template.Id.Where(char.IsLetterOrDigit).ToArray());
        idTail = idTail.Length > 8 ? idTail[^8..] : idTail;
        return $"{raw}-{idTail}.json";
    }
}
