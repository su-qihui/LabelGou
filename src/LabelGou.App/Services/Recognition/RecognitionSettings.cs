using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelGou.App.Services.Recognition;

/// <summary>
/// 识别通道的用户设置，存 <c>%APPDATA%\LabelGou\recognition.json</c>。
/// <para>两条默认值不是随手写的：<b>本地 OCR 默认开</b>（零依赖、离线、几十毫秒，没有理由关）；
/// <b>大模型默认也开但指向本机</b>（127.0.0.1），因为订单数据出网是用户明确关切的红线（§五-11）——
/// 端点一旦填成公网地址，界面必须把它显示出来并标"数据将离开这台电脑"。</para>
/// </summary>
public sealed class RecognitionSettings
{
    private const string AppFolderName = "LabelGou";
    private const string FileName = "recognition.json";

    /// <summary>端点主机名里出现这些词，界面就要打"数据出网"的红色标记。</summary>
    private static readonly string[] LocalHosts = { "127.0.0.1", "localhost", "0.0.0.0", "::1" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public bool UseLocalOcr { get; set; } = true;

    public bool UseVisionModel { get; set; } = true;

    /// <summary>Ollama 或任意 OpenAI 兼容服务的基地址。默认只指向本机。</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";

    /// <summary>本机实测可用的视觉模型名（3.3GB）。没有它时识别会降级为纯 OCR，不报错。</summary>
    public string Model { get; set; } = "qwen3-vl:4b";

    /// <summary>单次请求上限。实测 4B 视觉模型约 34 秒/张，冷启动加载模型还要更久。</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>OCR 语言；留空表示用系统里第一个可用识别包。</summary>
    public string? OcrLanguage { get; set; }

    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName, FileName);

    /// <summary>是否只碰本机：决定界面要不要提醒"数据出网"。</summary>
    [JsonIgnore]
    public bool StaysOnThisMachine =>
        Uri.TryCreate(Endpoint?.Trim() ?? string.Empty, UriKind.Absolute, out var uri)
        && LocalHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>给状态栏/核对窗口用的一行通道说明。</summary>
    public string DescribeChannels()
    {
        var parts = new List<string>();
        if (UseLocalOcr) parts.Add("本地 OCR（系统内置）");
        if (UseVisionModel)
        {
            var where = StaysOnThisMachine ? "本机" : "外部服务，数据会离开这台电脑";
            parts.Add($"大模型 {Model}（{where}）");
        }

        return parts.Count == 0 ? "未启用任何识别通道" : string.Join(" + ", parts);
    }

    public static RecognitionSettings Load() => LoadFrom(FilePath);

    /// <summary>从指定文件读。<b>单测走这条</b>，不往真用户的 <c>%APPDATA%</c> 里写东西。</summary>
    public static RecognitionSettings LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return new RecognitionSettings();
            var loaded = JsonSerializer.Deserialize<RecognitionSettings>(File.ReadAllText(path), JsonOptions);
            return loaded ?? new RecognitionSettings();
        }
        catch (Exception)
        {
            // 设置坏了不能拖累主流程：回到默认值，让用户在界面上重设一次。
            return new RecognitionSettings();
        }
    }

    public void Save() => SaveTo(FilePath);

    /// <summary>写到指定文件。目录不存在会自动建。</summary>
    public void SaveTo(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
