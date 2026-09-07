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

    /// <summary>
    /// 协议：<c>ollama</c> 走本机原生接口（/api/tags、/api/generate）；
    /// <c>openai</c> 走 OpenAI 兼容的 /chat/completions（阿里云百炼、DeepSeek、vLLM、Ollama 自己的 /v1 都是这一类）。
    /// <para>为什么不是一个地址自动探：两套协议的请求体形状不同（images 数组 vs content 里的 image_url），
    /// 猜错会报一堆看不出所以然的 400。填错协议时界面会直接说“端点像 X 但你选的是 Y”。</para>
    /// </summary>
    public string Provider { get; set; } = Providers.Ollama;

    /// <summary>云端密钥。<b>优先读环境变量</b>（见 <see cref="ApiKeyEnvVar"/>），这里只存用户自己填进设置界面的值。</summary>
    public string? ApiKey { get; set; }

    /// <summary>密钥的环境变量名。默认先查它，避开把密钥写进磁盘。</summary>
    public string ApiKeyEnvVar { get; set; } = "LABELGOU_LLM_KEY";

    /// <summary>
    /// 这个模型吃不吃图。DeepSeek 自家的 deepseek-chat <b>不支持图片</b>，
    /// 它只能接本地 OCR 认出的文字行（定案 D11 的“把 OCR 行当证据池”），所以关掉这项时
    /// 图片通道会明确报错而不是默默回一堆编出来的字段。
    /// </summary>
    public bool ModelAcceptsImages { get; set; } = true;

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

    /// <summary>
    /// 取真正生效的密钥：环境变量优先，其次才是存在设置里的那个。
    /// <para>优先环境变量不是洁癖：这台机的 <c>%APPDATA%\LabelGou\recognition.json</c> 是明文，
    /// 而店铺电脑会被人接手、也会被备份脚本扫走；能不放上去就不放。</para>
    /// </summary>
    public string? ResolveApiKey()
    {
        var fromEnv = Environment.GetEnvironmentVariable(ApiKeyEnvVar ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        return string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
    }

    /// <summary>两个常用云端的现成参数，省得用户手填地址填错。</summary>
    public static readonly CloudPreset[] CloudPresets =
    {
        new("阿里云百炼（qwen-vl-max，看图）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-vl-max", true),
        new("DeepSeek（deepseek-chat，只看文字）", "https://api.deepseek.com", "deepseek-chat", false),
    };

    /// <summary>把设置切到某个云端预设。</summary>
    public void ApplyPreset(CloudPreset preset)
    {
        Endpoint = preset.Endpoint;
        Model = preset.Model;
        Provider = Providers.OpenAi;
        ModelAcceptsImages = preset.AcceptsImages;
        // 云端一律把超时拉到 60 秒以上：公网往返 + 排队，180 秒是个不折腾人的上限。
        if (TimeoutSeconds < 60) TimeoutSeconds = 120;
    }

    /// <summary>协议名。只有两个值，所以用常量而不是枚举：设置文件里要能直接看懂。</summary>
    public static class Providers
    {
        public const string Ollama = "ollama";
        public const string OpenAi = "openai";
    }

    /// <summary>云端预设（名字、基地址、模型名、吃不吃图）。</summary>
    public sealed record CloudPreset(string Name, string Endpoint, string Model, bool AcceptsImages);

    /// <summary>给状态栏/核对窗口用的一行通道说明。</summary>
    public string DescribeChannels()
    {
        var parts = new List<string>();
        if (UseLocalOcr) parts.Add("本地 OCR（系统内置）");
        if (UseVisionModel)
        {
            var where = StaysOnThisMachine ? "本机" : "外部服务，数据会离开这台电脑";
            var eyes = ModelAcceptsImages ? "" : "· 不看图，只整理 OCR 文字";
            parts.Add($"{(Provider == Providers.OpenAi ? "云端" : "模型")} {Model}（{where}{eyes}）");
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
