using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LabelGou.App.Export;
using LabelGou.App.Services.Recognition;
using LabelGou.App.ViewModels;
using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// M6 识别链路的 App 层测试：真调 WinRT OCR、真走 HTTP（假端点）、真加载核对窗口、真换数据源。
/// <para><strong>夹具不是编的</strong>：脏 OCR 行来自 <c>labelgou-other\_probe\ocr-tfm\</c> 实跑落盘的
/// <c>ocr-zh-Hans-CN.txt</c>，模型 JSON 是 <c>ollama-out.txt</c> 里 <c>qwen3-vl:4b</c> 的真实返回
/// （含"JSON 全在 thinking 里而 response 为空"这个真形状）。用真样本是因为这些坑全是看了真样本才发现的。</para>
/// <para>窗口全程不 Show（没桌面会话也能跑），只量内容根；设置文件写在临时目录，不碰真用户的 <c>%APPDATA%</c>。</para>
/// </summary>
public class RecognitionFlowTests
{
    /// <summary>本机 OCR 对一张自渲染唛头图的真实输出（一个字没改，包括它读坏的符号）。</summary>
    private static readonly string[] OcrFixture =
    {
        "SHANGHAI · > LOS ANGELES, U SA",
        "C/S: MACYS （ 0 NTRACT NO: MM 2603",
        "PO NO: 2024 ． 0817 ITEM NO: A ． 778",
        "G.W.: 25 ． 5 KGS N .W.: 22 · 1 KGS",
        "M EAS: 60x40x30 CM CBM: 0 ． 072",
        "MADE IN CHINA",
        "No. 3 / 12",
        "上 海 到 洛 杉 矶 目 的 港",
    };

    /// <summary>同一张图，qwen3-vl:4b 的真实返回（第二次跑，consignee 串成了 MACYS）。</summary>
    private const string RealModelThinking =
        @"{""consignee"":""MACYS"",""clientCode"":""MACYS"",""contractNo"":""MMJ-2603"",""poNumber"":""2024-0817"",""itemNo"":""A-778"",""destinationPort"":""LOS ANGELES, USA"",""cartonNo"":""No. 3 / 12"",""cartonTotal"":""12"",""grossWeight"":""25.5 KGS"",""netWeight"":""22.1 KGS"",""measurement"":""60x40x30 CM"",""boxSize"":""0.072"",""origin"":""MADE IN CHINA"",""remarks"":""上海到洛杉矶 目的港""}";

    /// <summary>一份正常 Word 单据的正文（不带 OCR 噪声）。</summary>
    private static readonly string[] DocxLines =
    {
        "SHANGHAI -> LOS ANGELES, USA",
        "C/S: MACYS   CONTRACT NO: MMJ-2603",
        "PO NO: 2024-0817   ITEM NO: A-778",
        "G.W.: 25.5 KGS    N.W.: 22.1 KGS",
        "MEAS: 60x40x30 CM   CBM: 0.072",
        "MADE IN CHINA",
        "No. 3 / 12",
    };

    // ---------- 脚手架 ----------

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static void OnSta(Action work) => OnSta<object?>(() => { work(); return null; });

    private static string WithTemp(Action<string> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "labelgou-m6-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            body(folder);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { /* 临时目录清不掉不影响结论 */ }
        }
        return folder;
    }

    /// <summary>只按 URL 分流的假端点：/api/tags 报模型清单，/api/generate 给真样本 JSON。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string body;
            if (path.EndsWith("/api/tags", StringComparison.Ordinal))
            {
                body = """{"models":[{"name":"qwen3-vl:4b"},{"name":"llama3.2"}]}""";
            }
            else
            {
                if (request.Content is not null)
                    RequestBodies.Add(await request.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
                // 真形状：response 是空串，JSON 全在 thinking 里
                body = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["model"] = "qwen3-vl:4b",
                    ["response"] = string.Empty,
                    ["thinking"] = RealModelThinking,
                    ["done"] = true,
                });
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string WriteTinyPng(string folder)
    {
        var path = Path.Combine(folder, "单据.png");
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");
        File.WriteAllBytes(path, png);
        return path;
    }

    private static string WriteDocx(string folder, IEnumerable<string> lines)
    {
        var path = Path.Combine(folder, "单据.docx");
        var paragraphs = string.Concat(lines.Select(l =>
            "<w:p><w:r><w:t>" + WebUtility.HtmlEncode(l) + "</w:t></w:r></w:p>"));
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:body>" + paragraphs + "</w:body></w:document>";

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }

        return path;
    }

    private static RecognitionSettings OfflineSettings()
        => new() { UseLocalOcr = true, UseVisionModel = false };

    /// <summary>真样本拼一个批次：规则通道 + 模型通道各跑一遍，再交叉校验。</summary>
    private static RecognitionRun MakeRun(string sourceName = "单据.png")
    {
        var text = RecognizedText.FromLines(TextChannel.Ocr, sourceName, OcrFixture);
        var rules = RuleFieldExtractor.Extract(text);
        var llm = LlmFieldJsonParser.Parse(RealModelThinking, sourceName);
        var batch = new RecognizedBatch
        {
            SourceName = sourceName,
            TextChannel = TextChannel.Ocr,
            Text = text,
            RawModelPayload = RealModelThinking,
        };
        foreach (var field in CrossValidator.Merge(text, rules.Candidates, llm.Candidates))
            batch.Fields.Add(field);

        return new RecognitionRun
        {
            Capability = new RecognitionCapabilityReport
            {
                Ocr = new WindowsOcrReader.Capability(true, "zh-Hans-CN", null),
                ModelUsable = true,
                ModelReason = "模型 qwen3-vl:4b 可用",
                ModelName = "qwen3-vl:4b",
            },
            Batches = { batch },
        };
    }

    // ---------- 通道一：真调系统 OCR ----------

    [Fact]
    public void LocalOcrReallyReadsARenderedMarkLabel()
    {
        var capability = OnSta(WindowsOcrReader.Probe);
        Assert.True(capability.Available,
            "这台电脑没装中文 OCR 可选功能，这条测试的前提不成立：" + capability.Describe());

        WithTemp(dir =>
        {
            var path = Path.Combine(dir, "label.png");
            var text = OnSta(() =>
            {
                RenderLabelPng(path);
                return WindowsOcrReader.RecognizeFileAsync(path, null).GetAwaiter().GetResult();
            });

            Assert.True(text.LineCount >= 5, $"真识别只拿到 {text.LineCount} 行：{text.FullText}");
            Assert.Equal(TextChannel.Ocr, text.Channel);
            Assert.Contains(text.Lines, l => l.Text.Contains("CHINA", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(text.Lines, l => l.PixelWidth > 0 && l.PixelHeight > 0);   // 包围盒真填了
            Assert.NotEmpty(text.Warnings);                                           // 耗时那句一定有

            // 链路价值在这儿：OCR 把小数点读坏了，还得能抽出字段并把它修回来
            var found = RuleFieldExtractor.Extract(text).Candidates;
            var gross = found.FirstOrDefault(c => c.Field == MarkFieldKey.GrossWeight);
            Assert.NotNull(gross);
            Assert.Equal("25.5 KGS", FieldNormalizer.Normalize(MarkFieldKey.GrossWeight, gross!.RawValue).Value);
        });
    }

    /// <summary>画一张中英混排唛头图（与探针同一份文字），交给系统 OCR 认。</summary>
    private static void RenderLabelPng(string path)
    {
        const int width = 760;
        const int height = 420;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            AddText(dc, "SHANGHAI -> LOS ANGELES, USA", 34, 30);
            AddText(dc, "C/S: MACYS   CONTRACT NO: MMJ-2603", 26, 84);
            AddText(dc, "PO NO: 2024-0817   ITEM NO: A-778", 26, 126);
            AddText(dc, "G.W.: 25.5 KGS    N.W.: 22.1 KGS", 26, 168);
            AddText(dc, "MEAS: 60x40x30 CM   CBM: 0.072", 26, 210);
            AddText(dc, "MADE IN CHINA", 26, 252);
            AddText(dc, "No. 3 / 12", 30, 294);
            AddText(dc, "上海 到 洛杉矶   目的港", 24, 336);
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void AddText(DrawingContext dc, string text, double size, double y)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            size,
            Brushes.Black,
            1.0);
        dc.DrawText(formatted, new Point(28, y));
    }

    // ---------- 通道二：模型（假端点，真响应形状） ----------

    [Fact]
    public void ModelAnswerIsReadFromThinkingWhenResponseFieldIsEmpty()
    {
        var handler = new StubHandler();
        WithTemp(dir =>
        {
            var png = WriteTinyPng(dir);
            var settings = new RecognitionSettings { UseLocalOcr = false, UseVisionModel = true };

            var outcome = OllamaVisionClient.AskFieldsAsync(settings, png, default, handler)
                .GetAwaiter().GetResult();

            Assert.True(outcome.Ok, "模型通道失败：" + outcome.Error);
            Assert.Equal(string.Empty, outcome.RawResponse);           // 真形状：response 是空的
            Assert.NotNull(outcome.Json);
            Assert.Contains("MMJ-2603", outcome.Json!, StringComparison.Ordinal);

            var parsed = LlmFieldJsonParser.Parse(outcome.Json, "单据.png");
            Assert.Contains(parsed.Candidates, c => c.Field == MarkFieldKey.ClientCode && c.Origin == ValueOrigin.AiLlm);

            // 提示词必须带上字段白名单（枚举名原样），且请求里真的把图片 base64 与模型名送出去了
            // （提示词的中文在 JSON 里是被转义的，所以只能断言 ASCII 部分）
            var sent = handler.RequestBodies.FirstOrDefault() ?? string.Empty;
            Assert.Contains("ContractNo", sent, StringComparison.Ordinal);
            Assert.Contains("images", sent, StringComparison.Ordinal);
            Assert.Contains("qwen3-vl:4b", sent, StringComparison.Ordinal);
            Assert.Contains("temperature", sent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ModelOnlyValuesAreAllPinnedAsNoEvidenceAndUnconfirmed()
    {
        var handler = new StubHandler();
        WithTemp(dir =>
        {
            var png = WriteTinyPng(dir);
            var settings = new RecognitionSettings { UseLocalOcr = false, UseVisionModel = true };
            var run = RecognitionService.RunAsync(new[] { png }, settings, null, default, handler)
                .GetAwaiter().GetResult();

            Assert.True(run.Capability.ModelUsable, run.Capability.ModelReason);
            var batch = Assert.Single(run.Batches);
            Assert.NotEmpty(batch.Fields);
            Assert.Contains(batch.ChannelWarnings, w => w.Contains("关闭", StringComparison.Ordinal));
            Assert.All(batch.Fields, f => Assert.True(f.Confidence < 0.4, $"{f.Field} 只有模型一路、查无原文，置信度不该高"));
            Assert.All(batch.Fields, f => Assert.False(f.Confirmed));     // D13：一个都不许是已确认
            Assert.Equal(0, batch.BulkConfirm());                          // 一路独活 → 没有可批量放行的
        });
    }

    // ---------- Word 正文 + 落库 ----------

    [Fact]
    public void DocxRouteExtractsFieldsAndNeedsEveryLineConfirmedFirst()
    {
        WithTemp(dir =>
        {
            var docx = WriteDocx(dir, DocxLines);
            var run = RecognitionService.RunAsync(new[] { docx }, OfflineSettings()).GetAwaiter().GetResult();

            var batch = Assert.Single(run.Batches);
            Assert.Equal(TextChannel.DocxText, batch.TextChannel);
            Assert.True(batch.Fields.Count >= 6, $"真实正文只抽出 {batch.Fields.Count} 个字段");
            Assert.Contains(batch.Fields, f => f.Field == MarkFieldKey.ContractNo);
            Assert.False(batch.ReadyToImport);                             // 刚识别完一律待核
            Assert.True(batch.PendingCount > 0);
            Assert.NotNull(batch.Text);

            // 未确认也要能转成记录（带着 NeedsReview 去让预览标红），而不是默默丢掉
            var early = batch.ToRecord(1);
            Assert.NotEmpty(early.PendingReview());
            Assert.Contains("未人工核对", early.Get(MarkFieldKey.ContractNo)!.Warning, StringComparison.Ordinal);

            foreach (var field in batch.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
                field.Confirmed = true;
            Assert.True(batch.ReadyToImport, batch.DescribeProgress());

            var record = batch.ToRecord(1);
            Assert.Empty(record.PendingReview());
            Assert.True(record.Has(MarkFieldKey.ContractNo));
            Assert.Contains("单据.docx", record.SourceRef, StringComparison.Ordinal);
            Assert.Equal("MMJ-2603", record.GetText(MarkFieldKey.ContractNo));
        });
    }

    [Fact]
    public void UnsupportedAndMissingFilesAreSkippedWithWordsNotCrashes()
    {
        WithTemp(dir =>
        {
            var bogus = Path.Combine(dir, "老文档.doc");
            File.WriteAllText(bogus, "not a docx");
            var run = RecognitionService.RunAsync(
                new[] { bogus, Path.Combine(dir, "不存在.png") }, OfflineSettings()).GetAwaiter().GetResult();

            Assert.Empty(run.Batches);
            Assert.Contains(run.Warnings, w => w.Contains("跳过", StringComparison.Ordinal));
        });
    }

    // ---------- 核对窗口 ----------

    [Fact]
    public void ReviewWindowLoadsAndKeepsTheGateShutUntilEverythingIsConfirmed()
    {
        OnSta(() =>
        {
            var run = MakeRun();
            var vm = new RecognitionReviewViewModel(run);
            var window = new RecognitionReviewWindow(vm);

            Assert.NotNull(window.Content);
            Assert.Same(vm, window.DataContext);

            var root = (FrameworkElement)window.Content!;
            root.Measure(new Size(1240, 780));
            root.Arrange(new Rect(0, 0, 1240, 780));
            root.UpdateLayout();

            Assert.Equal(vm.Rows.Count, window.FieldDataGrid.Items.Count);
            Assert.True(window.FieldDataGrid.RenderSize.Height > 20, "字段表没拿到尺寸");
            Assert.Single(window.BatchListBox.Items);
            Assert.Equal(OcrFixture.Length, window.EvidenceListBox.Items.Count);   // 原文一行不丢
            Assert.Contains("实际走的通道", vm.ChannelLine, StringComparison.Ordinal);

            // 闸门：没核完就是不能导入（哪怕机器觉得很有把握）
            Assert.False(vm.ImportBatchCommand.CanExecute(null));
            vm.BulkConfirmCommand.Execute(null);
            Assert.False(vm.ImportBatchCommand.CanExecute(null), "批量确认之后一定还有剩下的要人看");

            foreach (var row in vm.Rows.Where(r => !string.IsNullOrWhiteSpace(r.Value)))
                row.Confirmed = true;
            Assert.True(vm.ImportBatchCommand.CanExecute(null));

            vm.ImportBatchCommand.Execute(null);
            Assert.Single(vm.ResultRecords);
            Assert.True(window.AnyImported);
            Assert.False(vm.ImportBatchCommand.CanExecute(null), "同一份不许导第二次");
            Assert.Equal(string.Empty, window.LeftoverSummary);
            window.Close();
        });
    }

    [Fact]
    public void EditingAConfirmedValueThrowsItBackIntoPending()
    {
        OnSta(() =>
        {
            var vm = new RecognitionReviewViewModel(MakeRun());
            var row = vm.Rows.First(r => !string.IsNullOrWhiteSpace(r.Value));
            row.Confirmed = true;
            Assert.True(row.Confirmed);
            var batch = vm.Batches[0];

            row.Value = row.Value + "改";

            Assert.False(row.Confirmed);
            Assert.Equal(1, row.Field.EditCount);
            Assert.True(batch.Batch.PendingCount > 0, "改了值却没退回待核 → 闸门会被骗过去");
            Assert.False(vm.ImportBatchCommand.CanExecute(null));
        });
    }

    [Fact]
    public void ClosingWithoutImportingLeavesAReadableSummary()
    {
        OnSta(() =>
        {
            var vm = new RecognitionReviewViewModel(MakeRun());
            Assert.False(vm.AnyImported);
            Assert.Contains("单据.png", vm.LeftoverSummary(), StringComparison.Ordinal);
        });
    }

    // ---------- 设置与主界面接线 ----------

    [Fact]
    public void SettingsRoundTripKeepsEndpointAndThePrivacyFlag()
    {
        WithTemp(dir =>
        {
            var path = Path.Combine(dir, "recognition.json");
            new RecognitionSettings
            {
                UseLocalOcr = false,
                Model = "llava:13b",
                Endpoint = "https://api.example.com",
                TimeoutSeconds = 45,
                OcrLanguage = "zh-Hans-CN",
            }.SaveTo(path);

            var loaded = RecognitionSettings.LoadFrom(path);
            Assert.False(loaded.UseLocalOcr);
            Assert.Equal("llava:13b", loaded.Model);
            Assert.Equal(45, loaded.TimeoutSeconds);
            Assert.Equal("zh-Hans-CN", loaded.OcrLanguage);
            Assert.False(loaded.StaysOnThisMachine);
            Assert.Contains("数据会离开这台电脑", loaded.DescribeChannels(), StringComparison.Ordinal);

            Assert.True(new RecognitionSettings().StaysOnThisMachine);          // 默认 127.0.0.1
            Assert.Contains("本机", new RecognitionSettings().DescribeChannels(), StringComparison.Ordinal);

            File.WriteAllText(path, "{ 这不是 JSON");
            Assert.True(RecognitionSettings.LoadFrom(path).UseLocalOcr);        // 坏了退回默认，不抛
        });
    }

    [Fact]
    public void AdoptingRecognizedRecordsTakesOverTheDataSource()
    {
        OnSta(() =>
        {
            // 必须注入临时目录的状态口：无参构造会拿 %APPDATA% 那份，测试就会往用户机器上写 uistate.json
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            var record = MakeRun().Batches[0].ToRecord(1);      // 未核对 → 带 NeedsReview

            vm.AdoptRecognizedRecords(new[] { record }, "单据.png");

            Assert.Equal(1, vm.RecordTotal);
            Assert.Contains("智能识别", vm.HeaderInfoText, StringComparison.Ordinal);
            Assert.True(vm.AiGateBlocked, "未核对字段必须让打印闸门亮起来");
            Assert.Null(vm.SourcePath);
            Assert.Empty(vm.IssueLines);
            Assert.False(vm.ApplyMappingCommand.CanExecute(null), "表格链路应当停用");

            // 空记录不能把数据源清空（用户点错一次不该丢东西）
            vm.AdoptRecognizedRecords(Array.Empty<MarkRecord>(), "空");
            Assert.Equal(1, vm.RecordTotal);
        });
    }
}
