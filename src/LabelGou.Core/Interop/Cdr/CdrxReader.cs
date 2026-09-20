using System.Text.Json;

namespace LabelGou.Core.Interop.Cdr;

/// <summary>
/// 读 <c>*.cdrx.json</c>。它是三个来源（驱动 CorelDRAW / 离线直解 .cdr / 读导出的 SVG）
/// 落到主软件的唯一入口，所以这里只负责"读得诚实"，不负责猜缺字段。
/// </summary>
public static class CdrxReader
{
    /// <summary>与写入端同一套：缩进、不转义中文、数字可按字符串读（旧 producer 干过这事）。</summary>
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    public static string PathFor(string cdrPath) => Path.ChangeExtension(cdrPath, ".cdrx.json");

    /// <summary>
    /// 读一份 cdrx。版本高于本程序支持的值就直接拒——**不许半读**：
    /// 半读会把新字段当成"没有"，把"这个来源还没告诉我"讲成"它不存在"。
    /// </summary>
    public static CdrxDoc ReadFile(string cdrxPath)
    {
        var text = File.ReadAllText(cdrxPath);
        CdrxDoc doc;
        try
        {
            doc = JsonSerializer.Deserialize<CdrxDoc>(text, Json)
                  ?? throw new InvalidDataException("cdrx 读不出内容：" + cdrxPath);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"这份 cdrx 不是合法 JSON（{e.Message}）：{cdrxPath}");
        }
        if (doc.FormatVersion > CdrxFormat.Version)
            throw new InvalidDataException(
                $"这份 cdrx 是 v{doc.FormatVersion}，当前软件只支持到 v{CdrxFormat.Version}，请升级 LabelGou 或重导这份设计：{Path.GetFileName(cdrxPath)}");
        return doc;
    }
}
