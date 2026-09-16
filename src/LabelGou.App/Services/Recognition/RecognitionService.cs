using System.IO;
using System.Net.Http;    // WPF 工程的隐式 using 里没有它（实测 CS0246），手写为准
using LabelGou.Core.Recognition;

namespace LabelGou.App.Services.Recognition;

/// <summary>
/// 一批识别用到的通道能力（跑之前探一次，把结论原样搬到界面上）。
/// <para>探一次而不是每个文件探一次，是因为模型不可用时每张图都要白等一轮连接超时：
/// 五张图就是五次「等 30 秒然后失败」，操作员只会以为程序卡死。</para>
/// </summary>
public sealed class RecognitionCapabilityReport
{
    /// <summary>本 Win7 变体不含本地 OCR 通道（Win7 系统没有 UWP OCR 引擎），恒为不可用。</summary>
    public bool OcrAvailable => false;

    /// <summary>本地 OCR 不可用的原因：本变体是"没有这条通道"，不是探测失败或缺语言包。</summary>
    public string OcrReason => "本 Win7 版不含本地 OCR 通道（Win7 系统没有该组件）；图片只走大模型，或改用 Word 文档。";

    /// <summary>本批是否真的会调用模型（设置开了 + 探得到才算）。</summary>
    public bool ModelUsable { get; init; }

    /// <summary>模型不可用的原因，或者「未启用」。给界面直接显示，不要写成"未知"。</summary>
    public string ModelReason { get; init; } = string.Empty;

    /// <summary>本批实际会启用的通道说明。</summary>
    public string ChannelsUsed(bool hasDocx, bool hasImage)
    {
        var parts = new List<string>();
        if (hasDocx) parts.Add("Word 正文直读");
        if (hasImage && ModelUsable) parts.Add($"大模型 {ModelName}");
        return parts.Count == 0 ? "没有可用通道" : string.Join(" + ", parts);
    }

    public string ModelName { get; init; } = string.Empty;

    /// <summary>因为通道不可用而降级的说明，需要让用户知道结果少了一路。</summary>
    public List<string> DegradedNotes()
    {
        var notes = new List<string>();
        notes.Add(OcrReason);   // 本变体恒无本地 OCR，这一路永久缺失，明说而不是静默
        if (!ModelUsable && ModelReason.Length > 0) notes.Add("大模型通道未启用：" + ModelReason);
        return notes;
    }
}

/// <summary>一次批处理的完整产出。</summary>
public sealed class RecognitionRun
{
    /// <summary>每个文件一个批次，顺序与传入一致（失败的也在，带告警）。</summary>
    public List<RecognizedBatch> Batches { get; } = new();

    public RecognitionCapabilityReport Capability { get; set; } = null!;

    /// <summary>批级告警（整批级别的降级说明），与批次内告警分开。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>用户中途取消：界面要提示"已完成的部分仍然有效"。</summary>
    public bool Cancelled { get; set; }

    public int HotFieldTotal => Batches.Sum(b => b.HotCount);

    public int PendingFieldTotal => Batches.Sum(b => b.PendingCount);

    public string Summarize()
        => $"{Batches.Count} 份文档，{Batches.Sum(b => b.Fields.Count)} 个字段，待核 {PendingFieldTotal} 项（其中 {HotFieldTotal} 项机器没把握）";
}

/// <summary>
/// 识别编排器 —— 把「文件 → 文本层 → 规则抽取 →（大模型）→ 交叉校验 → 可核对批次」串起来。
/// <para>三条硬规矩：</para>
/// <list type="number">
/// <item>任何一路挂了都不中断整批：失败变成那一份文档的告警，其它文件继续。</item>
/// <item>通道不可用就<strong>明说降级</strong>，绝不静默少一路还给出"看起来正常"的结果。</item>
/// <item>这里只产 <see cref="RecognizedBatch"/>，不产 <c>MarkRecord</c> 入库——中间必须经过人工核对窗口（定案 D13）。</item>
/// </list>
/// <para>Core 里的规则、归一化、交叉校验都是纯函数；本类只负责 IO 与调度，
/// 所以 M6 的判定逻辑全部在 <c>LabelGou.Core</c> 里被单测覆盖，这里只留少量胶水。</para>
/// </summary>
public static class RecognitionService
{
    /// <summary>能直接拖进来的图片类型（与打印常用格式对齐）。</summary>
    public static readonly IReadOnlyList<string> ImageExtensions =
        new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    /// <summary>能直读正文的文档类型。<c>.doc</c> 故意不收：它是 OLE 二进制，改扩展名混进来的概率极高。</summary>
    public static readonly IReadOnlyList<string> DocumentExtensions = new[] { ".docx" };

    /// <summary>文件选择框的过滤器。</summary>
    public static string OpenFilter =>
        "可识别的文件（Word / 图片）|*.docx;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp" +
        "|Word 文档|*.docx|图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|所有文件|*.*";

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsDocument(string path) => DocumentExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsSupported(string path) => IsImage(path) || IsDocument(path);

    /// <summary>跑之前探一次通道。本变体无本地 OCR 可探，只探模型（限时 30 秒内，见 <see cref="OllamaVisionClient.ProbeModelAsync"/>)。</summary>
    public static async Task<RecognitionCapabilityReport> ProbeAsync(
        RecognitionSettings settings,
        CancellationToken cancel = default,
        HttpMessageHandler? handler = null)
    {
        var modelUsable = false;
        string reason;
        if (!settings.UseVisionModel)
        {
            reason = string.Empty;   // 用户自己关的，不用反复提醒
        }
        else
        {
            var (found, why) = await OllamaVisionClient.ProbeModelAsync(settings, cancel, handler).ConfigureAwait(false);
            modelUsable = found;
            reason = found ? $"模型 {settings.Model} 可用" : why;
        }

        return new RecognitionCapabilityReport
        {
            ModelUsable = modelUsable,
            ModelReason = reason,
            ModelName = settings.Model,
        };
    }

    /// <summary>批量识别。串行执行：模型一张 34 秒，并发只会把本机显存打满，不会更快。</summary>
    public static async Task<RecognitionRun> RunAsync(
        IReadOnlyList<string> files,
        RecognitionSettings settings,
        IProgress<string>? progress = null,
        CancellationToken cancel = default,
        HttpMessageHandler? modelHandler = null)
    {
        var run = new RecognitionRun();
        var requested = files.Distinct(StringComparer.Ordinal).ToList();
        var missing = requested.Where(f => !File.Exists(f)).ToList();
        var existing = requested.Except(missing).ToList();

        // 光看“文件在不在”不够：老式 .doc 是 OLE 二进制，拖进来会被当成图片送进解码器，
        // 最后产出一份“0 个字段”的空文档。扩展名必须一起卡住。
        var supported = existing.Where(IsSupported).ToList();
        var skipped = existing.Except(supported).ToList();

        run.Capability = await ProbeAsync(settings, cancel, modelHandler).ConfigureAwait(false);
        foreach (var note in run.Capability.DegradedNotes()) run.Warnings.Add(note);
        foreach (var name in skipped.Concat(missing).Select(Path.GetFileName))
            run.Warnings.Add($"已跳过不支持或已不存在的文件：{name}");

        if (supported.Count == 0)
        {
            run.Warnings.Add("没有可识别的文件。");
            return run;
        }

        if (!settings.UseVisionModel && !supported.Any(IsDocument))
            run.Warnings.Add("模型通道被关掉了，而本 Win7 版又没有本地 OCR，图片识别不出任何东西。");

        var index = 0;
        foreach (var file in supported)
        {
            // 取消不是错：已经跑完的那几份要交回去，不能连着结果一起扔掉。
            if (cancel.IsCancellationRequested)
            {
                run.Cancelled = true;
                break;
            }
            index++;
            progress?.Report($"({index}/{supported.Count}) 正在识别 {Path.GetFileName(file)}…");

            RecognizedBatch batch;
            try
            {
                batch = await RecognizeOneAsync(file, settings, run.Capability, cancel, modelHandler).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                run.Cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                // 一份文档的意外不能带走整批：转成告警，剩下的继续。
                batch = new RecognizedBatch { SourceName = Path.GetFileName(file), SourcePath = file };
                batch.ChannelWarnings.Add($"这份文档识别失败：{ex.GetType().Name} {ex.Message}");
                AppLog.Error($"识别失败 {file}", ex);
            }

            run.Batches.Add(batch);
            progress?.Report($"({index}/{supported.Count}) {batch.SourceName}：{batch.DescribeProgress()}");
        }

        AppLog.Info($"识别完成：{run.Summarize()}（取消={run.Cancelled}）");
        return run;
    }

    /// <summary>识别一份文档。所有通道异常都在这里收敛成告警。</summary>
    public static async Task<RecognizedBatch> RecognizeOneAsync(
        string file,
        RecognitionSettings settings,
        RecognitionCapabilityReport capability,
        CancellationToken cancel = default,
        HttpMessageHandler? modelHandler = null)
    {
        var batch = new RecognizedBatch
        {
            SourceName = Path.GetFileName(file),
            SourcePath = file,
        };

        RecognizedText? text = null;
        if (IsDocument(file))
        {
            text = DocxTextReader.ReadFile(file);
        }
        else
        {
            // Win7 无本地 OCR：图片不产文字层，字段只能由吃图的云端模型直接给。
            // TextChannel.Llm 的语义正是"模型直给字段、不产文本行"，用它如实标记这一份。
            batch.TextChannel = TextChannel.Llm;
        }

        if (text is not null)
        {
            batch.TextChannel = text.Channel;
            batch.Text = text;
            foreach (var warning in text.Warnings) batch.ChannelWarnings.Add(warning);
        }

        var rules = RuleFieldExtractor.Extract(text);
        foreach (var warning in rules.Warnings) batch.ChannelWarnings.Add(warning);

        IReadOnlyList<FieldCandidate>? llmCandidates = null;
        if (settings.UseVisionModel && IsImage(file))
        {
            if (!capability.ModelUsable)
            {
                batch.ChannelWarnings.Add($"大模型没探到（{capability.ModelReason}），这一份只有文本层一路，交叉校验实际没生效。");
            }
            else if (!settings.ModelAcceptsImages)
            {
                // 本变体无本地 OCR，纯文本模型没有文字行可整理，图片识别不了——明说而不是喂 null 让它空转。
                batch.ChannelWarnings.Add("这个模型不吃图，而本 Win7 版没有本地 OCR 提供文字行，图片识别不了；换一家吃图的模型（如百炼 qwen-vl）或改用 Word 文档。");
            }
            else
            {
                var outcome = await OllamaVisionClient.AskFieldsAsync(settings, file, cancel, modelHandler).ConfigureAwait(false);
                batch.RawModelPayload = outcome.Json;
                if (!outcome.Ok)
                {
                    batch.ChannelWarnings.Add($"大模型这一路失败（{outcome.Elapsed.TotalSeconds:F1} 秒）：{outcome.Error}。结果只由文本层支撑，请逐条核对。");
                }
                else
                {
                    var parsed = LlmFieldJsonParser.Parse(outcome.Json, batch.SourceName);
                    foreach (var warning in parsed.Warnings) batch.ChannelWarnings.Add(warning);
                    llmCandidates = parsed.Candidates;
                    batch.ChannelWarnings.Add($"大模型：{parsed.Candidates.Count} 个字段值，用时 {outcome.Elapsed.TotalSeconds:F1} 秒。");
                }
            }
        }
        else if (settings.UseVisionModel && IsDocument(file))
        {
            batch.ChannelWarnings.Add("Word 文档不送模型（模型只看图），这一份只有正文直读一路。");
        }

        foreach (var field in CrossValidator.Merge(text, rules.Candidates, llmCandidates))
            batch.Fields.Add(field);

        if (batch.Fields.Count == 0)
            batch.ChannelWarnings.Add("一个字段都没识别出来。图太糊、拍斜了、或者这份文档里根本没有唛头内容。");

        return batch;
    }
}
