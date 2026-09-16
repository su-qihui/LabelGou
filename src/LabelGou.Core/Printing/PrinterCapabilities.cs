using System.Xml;
using System.Xml.Linq;

namespace LabelGou.Core.Printing;

/// <summary>
/// 驱动报出的一个可选项。<see cref="Keyword"/> 是写给驱动的值（可能带驱动私有前缀，如 ns0000:Upper），
/// <see cref="DisplayName"/> 是驱动自己的中文名——不自己翻译，是因为同一档纸在 Ricoh 叫「手送台」、
/// 在 Canon 叫「后端托盘」，翻错了操作员对不上号。
/// </summary>
public sealed record DriverOption(string Keyword, string DisplayName)
{
    /// <summary>去掉命名空间前缀后的尾巴（ns0000:Upper → Upper），用来跟托管枚举名对上。</summary>
    public string KeywordTail => Keyword.Contains(':') ? Keyword.Split(':', 2)[1] : Keyword;
}

/// <summary>驱动报出的一项功能（它自己的标签 + 可选值清单）。</summary>
public sealed record DriverSetting(string Feature, string DisplayName, IReadOnlyList<DriverOption> Options)
{
    /// <summary>「这台机器可选 …」那一行的内容。</summary>
    public string OptionsText => Options.Count == 0
        ? "（驱动没给出可选值）"
        : string.Join("　", Options.Select(o => o.DisplayName));

    /// <summary>把托管枚举名（Monochrome / Plain…）换成驱动给的名字；对不上就原样显示，不编。</summary>
    public string NameOf(string? bareKeyword)
    {
        if (string.IsNullOrEmpty(bareKeyword)) return null!;
        var hit = Options.FirstOrDefault(o =>
            string.Equals(o.KeywordTail, bareKeyword, StringComparison.OrdinalIgnoreCase));
        return hit?.DisplayName ?? bareKeyword;
    }
}

/// <summary>⑤ 步「这台打印机的出纸设置」里的一格。</summary>
public sealed record PrinterSettingRow(string Label, string CurrentText, string OptionsText, string Note);

/// <summary>
/// 解析驱动自己的 PrintCapabilities XML，取「颜色 / 纸张来源 / 介质类型」三项。
/// 纯逻辑：App 负责问驱动要 XML 与托管 ticket 里的当前值，这里只负责看懂与说话。
/// </summary>
public static class PrinterCapabilities
{
    public const string ColorFeature = "psk:PageOutputColor";
    public const string BinFeature = "psk:JobInputBin";
    public const string MediaTypeFeature = "psk:PageMediaType";

    private static readonly string[] Wanted = { ColorFeature, BinFeature, MediaTypeFeature };

    /// <summary>
    /// 从 capabilities XML 里取出关心的功能。驱动没报的功能**不会出现在结果里**——
    /// 调用方据此区分「驱动不给这项」和「驱动给了但值是空」，这两件事不能混着说。
    /// </summary>
    public static Dictionary<string, DriverSetting> Parse(string? capabilitiesXml)
    {
        var result = new Dictionary<string, DriverSetting>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(capabilitiesXml)) return result;

        XDocument doc;
        try
        {
            // 外部 XML 一律禁实体解析：驱动给的串里带 DTD 时不能让它去读本地文件
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(new StringReader(capabilitiesXml), settings);
            doc = XDocument.Load(reader);
        }
        catch (Exception)
        {
            return result;   // 读不懂就当驱动没报，界面会说清楚，不会假装一切正常
        }

        foreach (var feature in doc.Descendants().Where(e => e.Name.LocalName == "Feature"))
        {
            var name = (string?)feature.Attribute("name");
            if (name is null || !Wanted.Contains(name, StringComparer.Ordinal)) continue;
            result[name] = new DriverSetting(
                name,
                DisplayNameOf(feature) ?? FallbackLabel(name),
                feature.Descendants()
                    .Where(e => e.Name.LocalName == "Option")
                    .Select(e => new DriverOption(
                        (string?)e.Attribute("name") ?? string.Empty,
                        DisplayNameOf(e) ?? Tail((string?)e.Attribute("name"))))
                    .ToList());
        }
        return result;
    }

    /// <summary>
    /// 组三格。<paramref name="colorKeyword"/> / <paramref name="mediaTypeKeyword"/> 是托管 ticket 里的
    /// 当前值（枚举名）；某项功能驱动压根没报时，当前值一律说「驱动不报这一项」而不是拿默认值糊过去——
    /// 这台 RICOH 就是例子：ticket 里 PageMediaType 永远回 Plain，可驱动根本没有这个功能。
    /// </summary>
    public static List<PrinterSettingRow> BuildRows(
        IReadOnlyDictionary<string, DriverSetting> settings,
        string? colorKeyword,
        string? mediaTypeKeyword)
    {
        var rows = new List<PrinterSettingRow>();
        rows.Add(Row(settings, ColorFeature, "输出颜色", colorKeyword));
        rows.Add(Row(settings, BinFeature, "纸张来源", null));
        rows.Add(Row(settings, MediaTypeFeature, "纸张类型", mediaTypeKeyword));
        return rows;
    }

    private static PrinterSettingRow Row(
        IReadOnlyDictionary<string, DriverSetting> settings, string feature, string label, string? currentKeyword)
    {
        if (!settings.TryGetValue(feature, out var s))
        {
            return new PrinterSettingRow(label, "这台机器的驱动不报这一项", "—",
                "要改只能进驱动的打印首选项页，那里才有这一项。");
        }

        var current = currentKeyword is null
            ? "软件读不到，进「打印首选项…」看"
            : s.NameOf(currentKeyword) ?? "读不到";
        var howToChange = "要改点上面「打印首选项…」。";
        var note = $"驱动里这一项叫「{s.DisplayName}」。" + (currentKeyword is null
            ? "这一项软件读不回当前值：驱动把它存在自己的私有设置块里，公开字段不动。"
            : howToChange) + (s.Options.Count <= 1 ? "这台机器这一项只有一个可选值。" : string.Empty);
        return new PrinterSettingRow(label, current, s.OptionsText, note);
    }

    private static string? DisplayNameOf(XElement element) => element.Descendants()
        .Where(e => e.Name.LocalName == "Property" && (string?)e.Attribute("name") == "psk:DisplayName")
        .Select(e => e.Descendants().FirstOrDefault(v => v.Name.LocalName == "Value")?.Value)
        .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string FallbackLabel(string feature) => feature switch
    {
        ColorFeature => "颜色",
        BinFeature => "纸张来源",
        MediaTypeFeature => "介质类型",
        _ => Tail(feature),
    };

    private static string Tail(string? keyword) =>
        string.IsNullOrEmpty(keyword) ? "?" : (keyword.Contains(':') ? keyword.Split(':', 2)[1] : keyword);
}
