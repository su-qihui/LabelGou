using System.Text.Json;
using LabelGou.Core.Mapping;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Impos;

/// <summary>
/// 纸规库：内置种子（只读）+ 用户纸规（JSON 文件，<c>%APPDATA%\LabelGou\sheets</c>）。
/// <para>
/// 与 <see cref="TemplateStore"/> 同一套规矩：<strong>校验有 Error 一律拒绝入库</strong>，
/// 内置纸规不可覆盖、不可删除，改了要另存为用户纸规。
/// </para>
/// </summary>
public sealed class SheetSpecStore
{
    /// <summary>用户纸规目录。</summary>
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LabelGou",
        "sheets");

    private readonly string _directory;

    public SheetSpecStore(string? directory = null)
    {
        _directory = directory ?? Directory;
    }

    public string UserDirectory => _directory;

    /// <summary>全部纸规：内置在前，用户纸规按名称排序在后。</summary>
    public IReadOnlyList<SheetSpec> ListAll()
    {
        var list = new List<SheetSpec>(BuiltInSheetSpecs.All());
        if (System.IO.Directory.Exists(_directory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f))
            {
                var spec = TryRead(file);
                if (spec is not null) list.Add(spec);
            }
        }
        return list;
    }

    public SheetSpec? GetById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var builtIn = BuiltInSheetSpecs.GetById(id);
        if (builtIn is not null) return builtIn;
        return ListAll().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
    }

    /// <summary>保存用户纸规；校验不过直接拒。</summary>
    public (bool Saved, string? FileName, IReadOnlyList<TemplateIssue> Issues) Save(SheetSpec spec)
    {
        var issues = SheetSpecValidator.Validate(spec);
        if (issues.HasError()) return (false, null, issues);
        if (spec.BuiltIn)
        {
            return (false, null, new[]
            {
                new TemplateIssue(IssueLevel.Error, "内置纸规不可覆盖，请先「另存为用户纸规」。"),
            });
        }

        System.IO.Directory.CreateDirectory(_directory);
        var fileName = MakeFileName(spec);
        File.WriteAllText(Path.Combine(_directory, fileName), JsonSerializer.Serialize(spec, ProfileStore.JsonOptions));
        return (true, fileName, issues);
    }

    public bool Delete(string id)
    {
        if (BuiltInSheetSpecs.GetById(id) is not null) return false;
        var file = System.IO.Directory.Exists(_directory)
            ? System.IO.Directory.EnumerateFiles(_directory, "*.json")
                .FirstOrDefault(f =>
                {
                    var spec = TryRead(f);
                    return spec is not null && string.Equals(spec.Id, id, StringComparison.Ordinal);
                })
            : null;
        if (file is null) return false;
        File.Delete(file);
        return true;
    }

    private SheetSpec? TryRead(string path)
    {
        try
        {
            var spec = JsonSerializer.Deserialize<SheetSpec>(File.ReadAllText(path), ProfileStore.JsonOptions);
            if (spec is null) return null;
            spec.BuiltIn = false;
            // 有硬伤的旧文件不进列表，免得拼版算出离谱几何；靠日志/界面统计提示用户
            return SheetSpecValidator.Validate(spec).HasError() ? null : spec;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string MakeFileName(SheetSpec spec)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var raw = new string((spec.Name ?? "纸规").Where(ch => !invalid.Contains(ch) && !char.IsWhiteSpace(ch)).ToArray());
        if (raw.Length == 0) raw = "纸规";
        if (raw.Length > 50) raw = raw[..50];
        var idTail = new string(spec.Id.Where(char.IsLetterOrDigit).ToArray());
        idTail = idTail.Length > 8 ? idTail[^8..] : idTail;
        return $"{raw}-{idTail}.json";
    }
}
