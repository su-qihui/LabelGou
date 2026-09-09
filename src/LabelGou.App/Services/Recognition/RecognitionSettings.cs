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

    /// <summary>
    /// 云端密钥。<b>内存里的那一份</b>：不落盘（<c>[JsonIgnore]</c>），要存盘只能走 <see cref="RememberApiKey"/>
    /// 那条加密通道（<see cref="SecretStore"/>）。
    /// <para>上一版它会被明文写进 <c>%APPDATA%\LabelGou\recognition.json</c>（环境变量只是读时优先），
    /// 而这个目录会被备份脚本扫走、店铺电脑会被人接手——那是 §十-A-1 从第一天就想堵的口子。</para>
    /// <para>第 11 棒为何不直接删掉这个 <c>[JsonIgnore]</c>：用户报的是「填了就消失」，
    /// 而不是「请明文存盘」——两者不等价。加密存盘同一次运行内行为一致，仍然优先环境变量。</para>
    /// </summary>
    [JsonIgnore]
    public string? ApiKey { get; set; }

    /// <summary>
    /// 要不要把密钥存在这台电脑上（<b>DPAPI 密文</b>，单独文件 <c>llm-key.protected</c>，不写进本 JSON）。
    /// <para>默认 false：老设置文件升上来行为不变。它只在这台机、这个 Windows 登录用户下解得开，
    /// 拷到另一台机器就是一堆废纸——这是特性不是缺陷，界面上得把这句说出来。</para>
    /// </summary>
    public bool RememberApiKey { get; set; }

    /// <summary>本次从磁盘密文里读密钥的结果：<c>Missing</c>=没存过，<c>Unreadable</c>=存过但解不开（界面要红字）。</summary>
    [JsonIgnore]
    public SecretStore.Status SavedKeyStatus { get; internal set; } = SecretStore.Status.Missing;

    /// <summary>解不开时的那句人话（给界面直接用）。</summary>
    [JsonIgnore]
    public string? SavedKeyFailure { get; internal set; }

    /// <summary>
    /// 本次运行里用户在设置窗填过、但没勾「存在这台电脑」的那一串。
    /// <para>为什么要有它：设置窗与 AI 面板各自 <c>Load()</c> 出不同的对象，而 <see cref="ApiKey"/> 不落盘——
    /// 于是用户亲眼看到「设置窗里探活成功，对话窗里说没有密钥」（截图就是这个）。没有这一格，
    /// 界面上那句「只活在这次运行」就是假话：它其实只活在那一个窗口。</para>
    /// <para>只存内存，进程退出就没了；单测走的 <c>LoadFrom</c> 默认不合并它，免得静态字段造成测试串味。</para>
    /// </summary>
    public static string? SessionApiKey { get; set; }

    /// <summary>这份对象里的密钥是从 <see cref="SessionApiKey"/> 来的（界面要能说清是哪一路）。</summary>
    [JsonIgnore]
    public bool UsingSessionKey { get; internal set; }

    /// <summary>密钥现在从哪来：环境变量 / 本机存过 / 本次填的 / 根本没有。界面拿它写通道行，不再谎说「本次有效」。</summary>
    [JsonIgnore]
    public string ApiKeySource
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnvVar ?? string.Empty))) return "环境变量";
            if (UsingSessionKey && !string.IsNullOrWhiteSpace(ApiKey)) return "本次运行填的";
            if (!string.IsNullOrWhiteSpace(ApiKey)) return SavedKeyStatus == SecretStore.Status.Ok ? "本机存过" : "本次填的";
            return "没有密钥";
        }
    }

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

    /// <summary>
    /// 「收不到密钥」时的那句人话。四个出口共用一份：上一版它们是四句手写的近似话，
    /// 密钥能加密存盘之后全部过时了（还在叫用户去填一个不存在的「设置里的 apiKey」字段）。
    /// </summary>
    public string MissingKeyHint =>
        "没有可用的 API 密钥。到「模型（AI）设置与调试」里填进去（想下次还在就勾上「把密钥存在这台电脑上」），" +
        $"或设环境变量 {ApiKeyEnvVar}（环境变量优先）。";

    /// <summary>
    /// 几家的现成入口。注意：<strong>这里只锁地址不锁模型</strong>——
    /// 百炼背后一堆型号（qwen3 系列、qwen-vl 系列、flash/max 各档），写死一个就等于替用户做了错决定。
    /// 模型名靠拉列表选（<c>OllamaVisionClient.ListModelsAsync</c>），这里的 Model 只是拉到列表前的默认值。</summary>
    public static readonly CloudPreset[] CloudPresets =
    {
        new("阿里云百炼（DashScope，模型从列表里选）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-vl-max", null),
        new("DeepSeek（模型从列表里选）", "https://api.deepseek.com", "deepseek-chat", null),
    };

    /// <summary>
    /// 根据模型名猜它吃不吃图。OpenAI 兼容协议的 <c>/models</c> 一般只回 id，不吃图信息只能靠名字推。
    /// <para>认得出的几种：带 <c>vl</c> / <c>vision</c> / <c>omni</c> / <c>audio</c> 的多模态吃图；
    /// DeepSeek 的 <c>deepseek-chat</c>、<c>reasoner</c> 不吃。猜不出来时默认吃图（宁可在发图时报错，
    /// 也不要默默把图丢了只靠文字猜）。</para>
    /// </summary>
    public static bool GuessAcceptsImages(string? modelId)
    {
        var id = (modelId ?? string.Empty).ToLowerInvariant();
        if (id.Length == 0) return true;
        if (id.Contains("vl") || id.Contains("vision") || id.Contains("omni")
            || id.Contains("image") || id.Contains("multimodal") || id.Contains("qwen3.5")) return true;
        if (id.Contains("deepseek") || id.Contains("reasoner") || id.Contains("r1")) return false;
        return true;
    }

    /// <summary>把设置切到某个云端入口：地址与协议定下，模型名仍由用户从列表里挑。</summary>
    public void ApplyPreset(CloudPreset preset)
    {
        Endpoint = preset.Endpoint;
        Model = preset.Model;
        Provider = Providers.OpenAi;
        // 不再用预设里写死的吃图标记，而是按最终模型名猜：换成 qwen3-max 与 qwen-vl-max 待遇不同
        ModelAcceptsImages = GuessAcceptsImages(preset.Model);
        // 云端一律把超时拉到 60 秒以上：公网往返 + 排队，180 秒是个不折腾人的上限。
        if (TimeoutSeconds < 60) TimeoutSeconds = 120;
    }

    /// <summary>协议名。只有两个值，所以用常量而不是枚举：设置文件里要能直接看懂。</summary>
    public static class Providers
    {
        public const string Ollama = "ollama";
        public const string OpenAi = "openai";
    }

    /// <summary>
    /// 云端入口（名字、基地址、默认模型名）。<see cref="AcceptsImagesOverride"/> 为 null 表示
    /// “按模型名猜”，因为同一个账号里既有吃图的也有不吃图的型号。
    /// </summary>
    public sealed record CloudPreset(string Name, string Endpoint, string Model, bool? AcceptsImagesOverride);

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

    /// <summary>
    /// 界面走这条：除了读盘，还把本次运行里用户刚填的密钥接上（<see cref="SessionApiKey"/>）。
    /// <para>还要一项（第 20 棒，用户 2026-09-09 定「云端优先，Ollama 太慢」）：这台电脑上一个设置文件都没存过、
    /// 又确实有密钥可用时，直接就用云端预设。有密钥才改是因为没密钥就切云端等于把
    /// 所有 AI 功能变成「缺密钥」错；而用户存过配置就是他自己选过的，不抢方向盘。</para>
    /// </summary>
    public static RecognitionSettings Load()
    {
        var settings = LoadFrom(FilePath, SecretStore.DefaultFilePath, mergeSessionKey: true);
        if (!File.Exists(FilePath) && !string.IsNullOrWhiteSpace(settings.ResolveApiKey()))
            settings.ApplyPreset(CloudPresets[0]);
        return settings;
    }

    /// <summary>从指定文件读。<b>单测走这条</b>，不往真用户的 <c>%APPDATA%</c> 里写东西（也不碰密钥文件、不接静态会话密钥）。</summary>
    public static RecognitionSettings LoadFrom(string path) => LoadFrom(path, keyPath: null);

    /// <summary>
    /// 读设置，并且（只有给了 <paramref name="keyPath"/> 时）从那个文件里把存过的密钥解回内存。
    /// <param name="mergeSessionKey">是否把 <see cref="SessionApiKey"/> 接上。只有界面走 true：
    /// 本次刚填的那一串比磁盘上旧的那一串更贴近用户意图，所以盖过它。</param>
    /// </summary>
    public static RecognitionSettings LoadFrom(string path, string? keyPath, bool mergeSessionKey = false)
    {
        RecognitionSettings settings;
        try
        {
            if (!File.Exists(path)) settings = new RecognitionSettings();
            else
            {
                var loaded = JsonSerializer.Deserialize<RecognitionSettings>(File.ReadAllText(path), JsonOptions);
                settings = loaded ?? new RecognitionSettings();
            }
        }
        catch (Exception)
        {
            // 设置坏了不能拖累主流程：回到默认值，让用户在界面上重设一次。
            settings = new RecognitionSettings();
        }

        if (keyPath is not null) settings.ReadStoredKey(keyPath);
        if (mergeSessionKey && !string.IsNullOrWhiteSpace(SessionApiKey))
        {
            settings.ApiKey = SessionApiKey;
            settings.UsingSessionKey = true;
        }
        return settings;
    }

    /// <summary>
    /// 把磁盘上那份密文读回 <see cref="ApiKey"/>。<b>解不开不静默</b>：记下状态与原因交给界面说，
    /// 而不是当没存过（上一版就是「静默退回默认」让用户以为软件忘了他的密钥）。
    /// </summary>
    private void ReadStoredKey(string keyPath)
    {
        if (!RememberApiKey)
        {
            // 没勾保存就不去碰那个文件：万一文件是上一轮留下的，用户已经选了不存，再读出来等于替他做主。
            SavedKeyStatus = SecretStore.Status.Missing;
            SavedKeyFailure = null;
            return;
        }
        SavedKeyStatus = SecretStore.TryReadFile(keyPath, out var plain, out var failure);
        SavedKeyFailure = failure;
        if (SavedKeyStatus == SecretStore.Status.Ok && !string.IsNullOrWhiteSpace(plain)) ApiKey = plain;
    }

    /// <summary>把内存里这份密钥按开关同步到磁盘（写 / 删 / 不动）。</summary>
    private void SyncStoredKey(string keyPath)
    {
        if (!RememberApiKey)
        {
            SavedKeyFailure = SecretStore.TryClearFile(keyPath);      // 关了保存 = 磁盘上不留
            SavedKeyStatus = SecretStore.Status.Missing;
            return;
        }
        if (string.IsNullOrWhiteSpace(ApiKey)) return;                // 没新填的也不拿空值去覆盖存着的那份
        SavedKeyStatus = SecretStore.TryWriteFile(keyPath, ApiKey!.Trim(), out var failure);
        SavedKeyFailure = failure;
    }

    public void Save() => SaveTo(FilePath, SecretStore.DefaultFilePath);

    /// <summary>写到指定文件（只写设置，不碰密钥）。目录不存在会自动建。</summary>
    public void SaveTo(string path) => SaveTo(path, keyPath: null);

    /// <summary>写设置，并且（只有给了 <paramref name="keyPath"/> 时）按 <see cref="RememberApiKey"/> 同步密钥文件。</summary>
    public void SaveTo(string path, string? keyPath)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        if (keyPath is not null) SyncStoredKey(keyPath);
    }
}
