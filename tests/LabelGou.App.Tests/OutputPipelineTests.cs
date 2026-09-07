using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using LabelGou.App.Export;
using LabelGou.App.Printing;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Export;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// M3 输出链路的真实验证：真的渲染、真的编码、真的落盘，再把产物读回来核对。
/// <para>
/// 所有渲染都在 STA 后台线程里跑（与产品代码同一条路），顺便把 <see cref="StaWorker"/> 也验了。
/// </para>
/// </summary>
public class OutputPipelineTests
{
    private const double LabelW = 100;
    private const double LabelH = 80;
    private const double Margin = 8;

    private static SheetSpec Spec(CropMarkMode marks = CropMarkMode.None) => new()
    {
        Id = "test.a4.print",
        Name = "测试 A4（打印）",
        PaperWidthMm = 210,
        PaperHeightMm = 297,
        MarginLeftMm = Margin,
        MarginTopMm = Margin,
        MarginRightMm = Margin,
        MarginBottomMm = Margin,
        GutterXMm = 2,
        GutterYMm = 2,
        AllowRotate = false,
        RegistrationMarks = false,
        CropMarks = marks,
    };

    /// <summary>一个只画"整张标签边框"的模板：矩形线的位置可以精确手算，用来验毫米落位。</summary>
    private static LabelTemplate FrameTemplate() => new()
    {
        Id = "test.frame",
        Name = "测试框线",
        WidthMm = LabelW,
        HeightMm = LabelH,
        BorderMm = 0,
        CropMarkMm = 0,
        Elements =
        {
            new TemplateElement
            {
                Kind = ElementKind.Rect,
                X = 0,
                Y = 0,
                Width = LabelW,
                Height = LabelH,
                ThicknessMm = 0.5,
            },
        },
    };

    private static PageContentSource Source(int labelCount)
    {
        var records = Enumerable.Range(1, labelCount)
            .Select(_ => SampleRecords.StandardSample())
            .ToList();
        return new PageContentSource(FrameTemplate(), records, "C:/临时/样例.xlsx");
    }

    /// <summary>真模板 + 真样例数据：用于产出能肉眼复核的件（PDF 得能用阅读器打开）。</summary>
    private static PageContentSource RealSource()
    {
        var template = BuiltInTemplates.GetById(BuiltInTemplates.IdStandard)!;
        var records = Enumerable.Range(1, 9).Select(_ => SampleRecords.StandardSample()).ToList();
        return new PageContentSource(template, records, "E:/唛头样例数据_50条.xlsx");
    }

    private static T OnStaThread<T>(Func<T> work)
        => StaWorker.RunAsync((progress, token) => work(), null, CancellationToken.None)
            .GetAwaiter().GetResult();

    // ---------- 栅格化 ----------

    [Fact]
    public void PagePixelSizeMatchesRequestedDpi() => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var bitmap = PageRasterizer.RenderPage(plan, 1, 300, Source(4).AsProvider(), false, PageRenderPurpose.Image, true);

        // A4 @300DPI = 210/25.4*300 × 297/25.4*300 = 2480 × 3508
        Assert.Equal(2480, bitmap.PixelWidth);
        Assert.Equal(3508, bitmap.PixelHeight);
        Assert.Equal(300, bitmap.DpiX, 3);
        Assert.Equal(300, bitmap.DpiY, 3);
        return true;
    });

    [Fact]
    public void LabelBorderLandsWhereMillimetresSay() => OnStaThread(() =>
    {
        const int dpi = 300;
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var bitmap = PageRasterizer.RenderPage(plan, 1, dpi, Source(4).AsProvider(), false, PageRenderPurpose.Image, false);
        var pixels = Read(bitmap);

        // 第一枚标签左上角在 (8,8)mm，0.5mm 宽的描边中心应落在 8mm 处
        var expected = Mm.ToPixels(Margin, dpi);           // 94.49px
        var row = (int)Math.Round(Mm.ToPixels(Margin + LabelH / 2, dpi));   // 走一条横穿边框的行
        var left = CentreOfFirstDarkRun(pixels, bitmap.PixelWidth, row, 0, bitmap.PixelWidth / 2);
        Assert.True(Math.Abs(left - expected) <= 1.5,
            $"第一枚标签左边应在 {expected:0.#}px，实测 {left:0.#}px（容差 1.5px）");

        // 同一枚标签的右边在 8+100=108mm
        var expectedRight = Mm.ToPixels(Margin + LabelW, dpi);
        var right = CentreOfLastDarkRun(pixels, bitmap.PixelWidth, row, (int)(expectedRight - 30), bitmap.PixelWidth);
        Assert.True(Math.Abs(right - expectedRight) <= 2.0,
            $"第一枚标签右边应在 {expectedRight:0.#}px，实测 {right:0.#}px");
        return true;
    });

    [Fact]
    public void DoublingDpiDoublesPixelPositions() => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var source = Source(4);
        var low = PageRasterizer.RenderPage(plan, 1, 150, source.AsProvider(), false, PageRenderPurpose.Image, false);
        var high = PageRasterizer.RenderPage(plan, 1, 300, source.AsProvider(), false, PageRenderPurpose.Image, false);

        var rowLow = (int)Math.Round(Mm.ToPixels(Margin + LabelH / 2, 150));
        var rowHigh = (int)Math.Round(Mm.ToPixels(Margin + LabelH / 2, 300));
        var leftLow = CentreOfFirstDarkRun(Read(low), low.PixelWidth, rowLow, 0, low.PixelWidth / 2);
        var leftHigh = CentreOfFirstDarkRun(Read(high), high.PixelWidth, rowHigh, 0, high.PixelWidth / 2);

        Assert.True(Math.Abs(leftHigh - leftLow * 2) <= 2, $"300DPI 的落位应是 150DPI 的两倍：{leftHigh} vs {leftLow * 2}");
        return true;
    });

    [Fact]
    public void TrimMarksCanBeLeftOutOfOutput() => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(CropMarkMode.SheetCorners), LabelW, LabelH, 4);
        var source = Source(4);

        var withMarks = CountDark(Read(PageRasterizer.RenderPage(plan, 1, 150, source.AsProvider(), false, PageRenderPurpose.Image, true)), 150);
        var withoutMarks = CountDark(Read(PageRasterizer.RenderPage(plan, 1, 150, source.AsProvider(), false, PageRenderPurpose.Image, false)), 150);

        Assert.True(withMarks > withoutMarks, "开了裁切线应该多出深色像素");
        Assert.True(withoutMarks > 0, "关掉裁切线后标签本身仍要画出来");
        return true;
    });

    // ---------- 编码与落盘 ----------

    [Fact]
    public void PngExportKeepsExactPixelSizeAndIsReadable() => WithTempFolder(folder => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 6);
        var request = new SheetExportRequest
        {
            Plan = plan,
            Source = Source(6),
            PageIndexes = new[] { 0, 1 },
            BaseName = "测试批次<>|:*",          // 故意塞非法字符，必须被洗掉
            Dpi = 150,
        };
        var outcome = SheetExportService.ExportPngPages(request, folder, null, CancellationToken.None);

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(2, outcome.Files.Count);
        Assert.All(outcome.Files, f =>
        {
            var name = Path.GetFileName(f);
            Assert.StartsWith("测试批次", name);
            Assert.DoesNotContain("<", name);
            Assert.DoesNotContain(":", name);
        });
        Assert.Contains(outcome.Files, f => Path.GetFileName(f).EndsWith("_P01.png"));
        Assert.Contains(outcome.Files, f => Path.GetFileName(f).EndsWith("_P02.png"));

        var decoder = new PngBitmapDecoder(new Uri(outcome.Files[0]), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        Assert.Equal(PageRasterizer.PixelsForMillimetres(210, 150), frame.PixelWidth);
        Assert.Equal(PageRasterizer.PixelsForMillimetres(297, 150), frame.PixelHeight);
        Assert.Contains("PNG", outcome.Summary!);
        return true;
    }));

    [Fact]
    public void PdfExportWritesTwoPagesThatParseBackCleanly() => WithTempFolder(folder => OnStaThread(() =>
    {
        var path = Path.Combine(folder, "整版输出.pdf");
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 9);
        var request = new SheetExportRequest
        {
            Plan = plan,
            Source = RealSource(),
            PageIndexes = new[] { 0, 1 },
            BaseName = "整版输出",
            Dpi = 150,
        };
        var outcome = SheetExportService.ExportPdf(request, path, null, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > outcome.BytesWritten - 1 && bytes.Length == outcome.BytesWritten, "返回体积应与落盘一致");
        var text = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("/Count 2", text);
        Assert.Contains("/MediaBox [0 0 595.2756 841.8898]", text);      // A4
        Assert.Contains("/Producer (LabelGou ", text);                    // 带版本号
        Assert.Equal(2, CountOccurrences(text, "/Filter /DCTDecode"));
        return true;
    }));

    [Fact]
    public void LosslessPdfUsesFlateAndStillParses() => WithTempFolder(folder => OnStaThread(() =>
    {
        var path = Path.Combine(folder, "无损.pdf");
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var request = new SheetExportRequest
        {
            Plan = plan,
            Source = Source(4),
            PageIndexes = new[] { 0 },
            BaseName = "无损",
            Dpi = 150,
            RasterKind = PdfImageKind.Rgb24,
        };
        var outcome = SheetExportService.ExportPdf(request, path, null, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);

        var text = Encoding.ASCII.GetString(File.ReadAllBytes(path));
        Assert.Contains("/Filter /FlateDecode", text);
        Assert.Contains("/DecodeParms << /Colors 3 /BitsPerComponent 8 /Columns ", text);
        return true;
    }));

    [Fact]
    public void TiffExportCarriesOneFramePerPage() => WithTempFolder(folder => OnStaThread(() =>
    {
        var path = Path.Combine(folder, "多页.tif");
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var request = new SheetExportRequest
        {
            Plan = plan,
            Source = Source(4),
            PageIndexes = new[] { 0 },
            BaseName = "多页",
            Dpi = 150,
        };
        var outcome = SheetExportService.ExportTiff(request, path, null, CancellationToken.None);
        Assert.True(outcome.Success, outcome.Error);

        var decoder = new TiffBitmapDecoder(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        Assert.Single(decoder.Frames);
        Assert.True(decoder.Frames[0].PixelWidth > 1000);
        return true;
    }));

    [Fact]
    public void TiffRefusesTooManyPagesWithActionableMessage() => WithTempFolder(folder => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, SheetExportService.MaxTiffFrames * 4);
        var request = new SheetExportRequest
        {
            Plan = plan,
            Source = Source(plan.LabelCount),
            PageIndexes = Enumerable.Range(0, Math.Min(plan.PageCount, SheetExportService.MaxTiffFrames + 5)).ToList(),
            BaseName = "太多页",
            Dpi = 150,
        };
        var outcome = SheetExportService.ExportTiff(request, Path.Combine(folder, "x.tif"), null, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("改用 PDF", outcome.Error!);
        return true;
    }));

    [Fact]
    public void BadRequestsAreRejectedBeforeTouchingTheDisk() => WithTempFolder(folder =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var request = new SheetExportRequest
        {
            Plan = plan,
            Source = Source(4),
            PageIndexes = Array.Empty<int>(),
            BaseName = "空",
            Dpi = 30,
        };
        var outcome = SheetExportService.ExportPdf(request, Path.Combine(folder, "不该存在.pdf"), null, CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("DPI", outcome.Error!);
        Assert.False(File.Exists(Path.Combine(folder, "不该存在.pdf")));

        var outOfRange = new SheetExportRequest
        {
            Plan = plan,
            Source = Source(4),
            PageIndexes = new[] { 999 },
            BaseName = "越界",
            Dpi = 300,
        };
        var second = SheetExportService.ExportPngPages(outOfRange, folder, null, CancellationToken.None);
        Assert.False(second.Success);
        Assert.Contains("超出整版页数", second.Error!);
        return true;
    });

    // ---------- 打印（本机没有实体打印机，只验不需要硬件的部分） ----------

    [Fact]
    public void FilePrinterDetectionCoversPortPrompt()
    {
        Assert.True(PrintService.LooksLikeFilePrinter("Microsoft Print to PDF", "PORTPROMPT:"));
        Assert.True(PrintService.LooksLikeFilePrinter("任意名字", "PORTPROMPT:"), "端口是 PORTPROMPT 就该拦，与名字无关");
        Assert.True(PrintService.LooksLikeFilePrinter("导出为WPS PDF", "Kingsoft Virtual Printer Port"));
        Assert.False(PrintService.LooksLikeFilePrinter("HP LaserJet 400", "USB001"));
    }

    [Fact]
    public void PrinterEnumerationEitherReturnsQueuesOrExplainsWhyNot()
    {
        var printers = PrintService.ListPrinters(out var error);
        Assert.True(printers.Count > 0 || !string.IsNullOrWhiteSpace(error),
            "读到列表或给出人话原因，不能两者都空");
        Assert.All(printers, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.NotNull(printers.FirstOrDefault(p => p.IsDefault)?.DisplayName);
    }

    [Fact]
    public void PrintRejectsImpossibleJobsWithoutOpeningAPrinter()
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var outcome = PrintService.Print(new PrintRequest
        {
            Plan = plan,
            Source = Source(4),
            PageIndexes = Array.Empty<int>(),
            Copies = 0,
        }, null, CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Contains("没有选中任何一页", outcome.Error!);
        Assert.Contains("份数", outcome.Error!);
        Assert.Equal(0, outcome.SheetsSent);
    }

    [Fact]
    public void PrintFailsGracefullyWhenPrinterNameIsNonsense()
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var outcome = OnStaThread(() => PrintService.Print(new PrintRequest
        {
            Plan = plan,
            Source = Source(4),
            PageIndexes = new[] { 0 },
            PrinterName = "这台机器根本不存在",
        }, null, CancellationToken.None));

        Assert.False(outcome.Success);
        Assert.Contains("找不到打印机", outcome.Error!);
    }

    [Fact]
    public void ProbeFitAsksTheRealDriverWithoutSendingAJob() => OnStaThread(() =>
    {
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var advice = PrintService.ProbeFit(new PrintRequest
        {
            Plan = plan,
            Source = Source(4),
            PageIndexes = new[] { 0 },
            PrinterName = null,          // 默认打印机
        }, out var error);

        // 这台机器上默认是“Microsoft Print to PDF”；没装任何打印机时也不得抛异常
        Assert.True(advice.Level != PrintFitLevel.Rejected || advice.Warnings.Count > 0,
            "要么判得下，要么把理由说清");
        Assert.True(string.IsNullOrEmpty(error) || error.Contains("打印机"), error);
        Assert.False(string.IsNullOrWhiteSpace(advice.Describe("整版")));
        return true;
    });

    // ---------- 复核闸门（§七-11） ----------

    [Fact]
    public void ReviewGateSeesFlaggedValuesBeforePrinting()
    {
        var template = BuiltInTemplates.GetById(BuiltInTemplates.IdStandard)!;
        var clean = SampleRecords.StandardSample();
        var suspect = clean.ToBuilder()
            .Set(MarkFieldKey.GrossWeight, new MarkValue("18.50", ValueOrigin.ExcelImport).WithWarning("毛重疑似与净重倒挂，请核对"))
            .Build();

        Assert.Equal(0, new PageContentSource(template, new[] { clean, clean }, "x.xlsx").UnconfirmedLabelCount);

        var mixed = new PageContentSource(template, new[] { clean, suspect }, "x.xlsx");
        Assert.Equal(1, mixed.UnconfirmedLabelCount);
        // 快照不可变 → 结果缓存，不能因为“算第二次不一样”而放过错号
        Assert.Equal(1, mixed.UnconfirmedLabelCount);
    }

    // ---------- 小工具 ----------

    private static T WithTempFolder<T>(Func<string, T> body)
    {
        // 设上这个环境变量就会把产物留在指定目录里不删，方便肉眼与外部阅读器复核（人工验收用）
        var keep = Environment.GetEnvironmentVariable("LABELGOU_TEST_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(keep))
        {
            Directory.CreateDirectory(keep);
            return body(keep);
        }

        var folder = Path.Combine(Path.GetTempPath(), "labelgou-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            return body(folder);
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { /* 测试产物删不掉不影响结论 */ }
        }
    }

    // ---------- 整版上不许多出来的东西（批次二） ----------

    /// <summary>一个什么都不画的模板：纸面上除了拼版辅助线不该有第二样东西。</summary>
    private static LabelTemplate BlankTemplate() => new()
    {
        Id = "test.blank",
        Name = "空白模板",
        WidthMm = LabelW,
        HeightMm = LabelH,
        BorderMm = 0,
        CropMarkMm = 0,
    };

    private static PageContentSource BlankSource(int labelCount)
        => new(BlankTemplate(), Enumerable.Range(1, labelCount).Select(_ => SampleRecords.StandardSample()).ToList(), "样例.xlsx");

    [Fact]
    public void 整版不给每枚标签铺白底也不描那圈灰框() => OnStaThread(() =>
    {
        // LabelRenderer 那 1DIU 的 (160,160,160) 灰框是单枚预览用的；整版一铺就有两个后果：
        // 打印/PDF/PNG 上多一道脏线（SVG 出口又没有），而且标签矩形内的套准十字被盖掉。
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var bitmap = PageRasterizer.RenderPage(plan, 1, 150, BlankSource(4).AsProvider(), false, PageRenderPurpose.Image, false);
        var pixels = Read(bitmap);
        var width = bitmap.PixelWidth;
        var gray = 0;
        for (var y = 0; y < bitmap.PixelHeight; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (Math.Abs(pixels[i] - 160) < 26 && Math.Abs(pixels[i + 1] - 160) < 26 && Math.Abs(pixels[i + 2] - 160) < 26) gray++;
            }
        }

        Assert.Equal(0, gray);
        return true;
    });

    [Fact]
    public void 打印请求也拦页号越界()
    {
        // 导出端一直有这一项，打印端上一版漏了：选错页会静默少打
        var plan = ImpositionEngine.Build(Spec(), LabelW, LabelH, 4);
        var request = new PrintRequest
        {
            Plan = plan,
            Source = Source(4),
            PageIndexes = new[] { plan.PageCount },       // 页号 0 起始 → 这一页不存在
        };

        var issues = new List<string>();
        request.CollectIssues(issues);

        Assert.Contains(issues, i => i.Contains("超出整版页数", StringComparison.Ordinal));
    }

    [Fact]
    public void 纸规错误也写进出闸门的文案()
    {
        Assert.Null(ExportViewModel.ComposeGateMessage(10, 0, 0));

        var onlySheetErrors = ExportViewModel.ComposeGateMessage(10, 0, 2);
        Assert.NotNull(onlySheetErrors);
        Assert.Contains("2 条标成错误的纸规", onlySheetErrors, StringComparison.Ordinal);

        var both = ExportViewModel.ComposeGateMessage(10, 3, 2);
        Assert.Contains("3 张含「需人工核对」", both!, StringComparison.Ordinal);
        Assert.Contains("2 条标成错误的纸规", both!, StringComparison.Ordinal);
    }

    private static byte[] Read(RenderTargetBitmap bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static bool IsDark(byte[] bgra, int x, int y, int width)
    {
        var index = (y * width + x) * 4;      // Pbgra32：B,G,R,A
        return bgra[index] < 140 && bgra[index + 1] < 140 && bgra[index + 2] < 140;
    }

    /// <summary>一行里第一段连续深色像素的中点（描边中心），用来定位置而不是看有没有黑点。</summary>
    private static double CentreOfFirstDarkRun(byte[] bgra, int width, int y, int from, int to)
    {
        var strideWidth = width;
        var start = -1;
        for (var x = from; x < to; x++)
        {
            if (IsDark(bgra, x, y, strideWidth)) { start = x; break; }
        }
        Assert.True(start >= 0, $"第 {y} 行在 [{from},{to}) 内没有找到深色像素");
        var end = start;
        for (var x = start + 1; x < to; x++)
        {
            if (!IsDark(bgra, x, y, strideWidth)) break;
            end = x;
        }
        return (start + end) / 2.0;
    }

    private static double CentreOfLastDarkRun(byte[] bgra, int width, int y, int from, int to)
    {
        var last = -1;
        for (var x = from; x < to; x++)
        {
            if (IsDark(bgra, x, y, width)) last = x;
        }
        Assert.True(last >= 0, $"第 {y} 行在 [{from},{to}) 内没有找到深色像素");
        var start = last;
        while (start > from && IsDark(bgra, start - 1, y, width)) start--;
        return (start + last) / 2.0;
    }

    private static int CountDark(byte[] bgra, int dpi)
    {
        // 只数左侧 1/3 区域，避开右边可能的抗锯齿噪声
        var width = (int)Math.Round(Mm.ToPixels(210, dpi));
        var height = (int)Math.Round(Mm.ToPixels(297, dpi));
        var count = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width / 3; x++)
            {
                if (IsDark(bgra, x, y, width)) count++;
            }
        }
        return count;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
