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
    /// <summary>用户纸规目录（根目录走 <see cref="UserPaths"/>，单测能换走它）。</summary>
    public static string Directory => Path.Combine(UserPaths.Root, "sheets");

    private readonly string _directory;

    public SheetSpecStore(string? directory = null)
    {
        _directory = directory ?? Directory;
    }

    public string UserDirectory => _directory;

    /// <summary>全部纸规：内置在前，用户纸规按名称排序在后。</summary>
    public IReadOnlyList<SheetSpec> ListAll() => ListWithReport().Specs;

    /// <summary>
    /// 列目录并且把“那份没进来”一并带回去。
    /// <para>上一版只 <c>return null</c> 然把 JSON 语法错吃掉，用户另存的纸规就这样静默蒸发（§五-64
    /// 同类问题）：Core 不引用界面也不写日志，所以把名单交给调用方去弹。</para>
    /// </summary>
    public SheetListReport ListWithReport()
    {
        var list = new List<SheetSpec>(BuiltInSheetSpecs.All());
        var skipped = new List<string>();
        if (System.IO.Directory.Exists(_directory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f))
            {
                var (spec, reason) = TryRead(file);
                if (spec is not null) list.Add(spec);
                else skipped.Add($"{Path.GetFileName(file)}：{reason}");
            }
        }
        return new SheetListReport(list, skipped);
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
                    var (spec, _) = TryRead(f);
                    return spec is not null && string.Equals(spec.Id, id, StringComparison.Ordinal);
                })
            : null;
        if (file is null) return false;
        File.Delete(file);
        return true;
    }

    private static (SheetSpec? Spec, string? Reason) TryRead(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"文件读不出来（{ex.GetType().Name}）");
        }

        SheetSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<SheetSpec>(json, ProfileStore.JsonOptions);
        }
        catch (JsonException ex)
        {
            return (null, $"JSON 格式不对（{ex.Message}）");
        }

        if (spec is null) return (null, "内容不是纸规");
        spec.BuiltIn = false;
        // 有硬伤的旧文件不进列表，免得拼版算出离谱几何；但「哪个文件、为什么」必须能被人看见：
        // Core 不写日志也不碰界面，所以把原因带回去交给调用方（上一版这里是默默 return null）。
        var error = SheetSpecValidator.Validate(spec).FirstOrDefault(i => i.Severity == IssueLevel.Error);
        return error is null ? (spec, null) : (null, $"纸规本身不合法（{error.Message}）");
    }

    /// <summary>一次列目录的结果：能用的纸规 + 被跳过的文件（文件名与原因，直接能拼进界面提示）。</summary>
    public sealed record SheetListReport(IReadOnlyList<SheetSpec> Specs, IReadOnlyList<string> SkippedFiles);

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
