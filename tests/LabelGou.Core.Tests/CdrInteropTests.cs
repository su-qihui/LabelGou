using System.IO.Compression;
using System.Text;
using LabelGou.Core.Interop.Cdr;
using LabelGou.Core.Interop.Svg;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// M5 · 底稿导入的两条来路（SVG 与 .cdr 内嵌预览）与 C 类模板落库。
/// <para>
/// 本机没有 CorelDRAW，所以 .cdr 的样本是<strong>按规范合成的</strong>：这能证明解码逻辑自洽，
/// 但<strong>不能证明真文件长这样</strong>——真样本待用户从店里带回来（对接文档 §六 M5 待验收清单）。
/// </para>
/// </summary>
public class CdrInteropTests
{
    private static byte[] LE(int value) => BitConverter.GetBytes(value);

    private static byte[] BuildDisp(int width, int height, byte[] palette, byte[] indices, bool bottomUp = true)
    {
        var disp = new byte[44 + palette.Length + indices.Length];
        // 4B 未知 + 头长 40 + 宽 + 高（BMP 惯例：正数=自下而上）+ 28B DIB 剩余
        BitConverter.GetBytes(40).CopyTo(disp, 4);
        BitConverter.GetBytes(width).CopyTo(disp, 8);
        BitConverter.GetBytes(bottomUp ? height : -height).CopyTo(disp, 12);
        BitConverter.GetBytes((ushort)1).CopyTo(disp, 16);   // planes
        BitConverter.GetBytes((ushort)8).CopyTo(disp, 18);   // bitcount
        palette.CopyTo(disp, 44);
        indices.CopyTo(disp, 44 + palette.Length);
        return disp;
    }

    private static byte[] BuildRiff(ushort version, byte[]? disp)
    {
        using var payload = new MemoryStream();
        void Chunk(string id, byte[] data)
        {
            payload.Write(Encoding.ASCII.GetBytes(id));
            payload.Write(LE(data.Length));
            payload.Write(data);
            if (data.Length % 2 != 0) payload.WriteByte(0);   // chunk 偶数字节对齐
        }

        Chunk("vrsn", new byte[] { (byte)(version & 0xFF), (byte)(version >> 8), 0, 0 });
        if (disp is not null) Chunk("DISP", disp);

        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("RIFF"));
        ms.Write(LE((int)payload.Length + 4));
        ms.Write(Encoding.ASCII.GetBytes("CDR "));
        payload.Position = 0;
        payload.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] BuildZip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var target = entry.Open();
                target.Write(data);
            }
        }
        return ms.ToArray();
    }

    private static byte[] FakePng(int seed) => new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 0, (byte)seed };

    // ---------- .cdr：ZIP 路线 ----------

    [Fact]
    public void CdrZipThumbnailEntryIsExtractedWhole()
    {
        var bytes = BuildZip(("content.xml", new byte[] { 1 }), ("thumbnail.png", FakePng(7)), ("metadata.json", new byte[] { 2 }));
        var result = CdrPreviewReader.Read(new MemoryStream(bytes));

        Assert.True(result.HasPreview, string.Join(" | ", result.ErrorMessages));
        var preview = result.Preview!;
        Assert.Equal(CdrPreviewKind.EncodedImage, preview.Kind);
        Assert.Contains("thumbnail.png", preview.Origin, StringComparison.Ordinal);
        Assert.Equal(FakePng(7), preview.EncodedBytes);
    }

    [Fact]
    public void CdrZipPrefersRealBitmapOverWmfThumbnail()
    {
        var bytes = BuildZip(("thumbnail.wmf", new byte[] { 0, 1 }), ("thumbnail.jpeg", FakePng(9)));
        var result = CdrPreviewReader.Read(new MemoryStream(bytes));
        Assert.Contains("jpeg", result.Preview!.Origin, StringComparison.Ordinal);
    }

    [Fact]
    public void CdrZipWithoutAnyEntryFallsThroughToHonestFailure()
    {
        var bytes = BuildZip(("content/riffData.cdr", new byte[] { 1, 2, 3 }));
        var result = CdrPreviewReader.Read(new MemoryStream(bytes));
        Assert.False(result.HasPreview);
        Assert.Contains(result.ErrorMessages, m => m.Contains("thumbnail", StringComparison.Ordinal));
        // 失败话术里必须教用户怎么拿到保真件，而不是只说"读不了"
        Assert.Contains(result.ErrorMessages, m => m.Contains("SVG", StringComparison.Ordinal));
    }

    // ---------- .cdr：裸 RIFF 路线 ----------

    [Fact]
    public void CdrRiffDispFlipsBottomUpRowsIntoTopDownPixels()
    {
        var palette = new byte[1024];
        for (var i = 0; i < 256; i++)
        {
            palette[i * 4] = 0;
            palette[i * 4 + 1] = 0;
            palette[i * 4 + 2] = (byte)(i * 10 % 256);
        }
        // 画面：第一行索引 1、第二行索引 2；源里按 BMP 惯例反过来写
        var indices = new byte[] { 2, 2, 1, 1 };
        var disp = BuildDisp(2, 2, palette, indices, bottomUp: true);
        var result = CdrPreviewReader.Read(new MemoryStream(BuildRiff(1400, disp)));

        var preview = result.Preview;
        Assert.NotNull(preview);
        Assert.Equal(CdrPreviewKind.Argb32, preview!.Kind);
        Assert.Equal(2, preview.Width);
        Assert.Equal(2, preview.Height);
        Assert.Equal(1400, preview.VersionHint);
        Assert.Contains("riff:DISP", preview.Origin, StringComparison.Ordinal);

        // 输出自上而下：第一行该是索引 1 的颜色，第二行是索引 2（一行有 2 个像素 = 8 字节）
        Assert.Equal((byte)(1 * 10 % 256), preview.Pixels![2]);
        Assert.Equal((byte)(2 * 10 % 256), preview.Pixels![8 + 2]);
        Assert.Equal(255, preview.Pixels![3]);  // alpha 一律不透明
    }

    [Fact]
    public void CdrRiffTopDownFlagIsHonoured()
    {
        var palette = new byte[1024];
        palette[1 * 4 + 2] = 11;
        palette[2 * 4 + 2] = 22;
        var disp = BuildDisp(1, 2, palette, new byte[] { 1, 2 }, bottomUp: false); // 负高 = 自上而下
        var preview = CdrPreviewReader.Read(new MemoryStream(BuildRiff(1500, disp))).Preview;
        Assert.NotNull(preview);
        Assert.Equal(11, preview!.Pixels![2]);
        Assert.Equal(22, preview.Pixels[4 + 2]);
    }

    [Fact]
    public void CdrRiffWithoutDispDoesNotPretend()
    {
        var result = CdrPreviewReader.Read(new MemoryStream(BuildRiff(1400, null)));
        Assert.False(result.HasPreview);
        Assert.True(result.HasError);
        Assert.Contains(result.ErrorMessages, m => m.Contains("DISP", StringComparison.Ordinal));
    }

    [Fact]
    public void CdrDispWithAbsurdDimensionsIsRefused()
    {
        var disp = BuildDisp(100000, 100000, new byte[1024], new byte[4]);
        var result = CdrPreviewReader.Read(new MemoryStream(BuildRiff(1400, disp)));
        Assert.False(result.HasPreview);
        Assert.True(result.HasError, "像素数爆表的 DISP 必须拒掉，不能先把内存吃光");
    }

    [Theory]
    [InlineData("WL12", "1992")]
    [InlineData("XXYY", "不像")]
    public void CdrNonReadableFilesGetAReasonAndAWayOut(string magic, string expectedFragment)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(magic));
        bytes.AddRange(Enumerable.Repeat((byte)0x20, 64));
        var result = CdrPreviewReader.Read(new MemoryStream(bytes.ToArray()));
        Assert.False(result.HasPreview);
        Assert.Contains(result.ErrorMessages, m => m.Contains(expectedFragment, StringComparison.Ordinal));
    }

    [Fact]
    public void CdrTinyFileDoesNotThrow()
        => Assert.True(CdrPreviewReader.Read(new MemoryStream(new byte[] { 1, 2 })).HasError);

    [Fact]
    public void CdrMissingFileReturnsErrorNotException()
    {
        var result = CdrPreviewReader.ReadFile(Path.Combine(Path.GetTempPath(), "labelgou-无此文件-" + Guid.NewGuid().ToString("N") + ".cdr"));
        Assert.True(result.HasError);
    }

    [Theory]
    [InlineData(1400, "X4")]
    [InlineData(1700, "X7")]
    [InlineData(null, "未知")]
    public void CdrVersionIsExplainedInHumanWords(int? version, string fragment)
        => Assert.Contains(fragment, CdrPreviewReader.DescribeVersion(version), StringComparison.Ordinal);

    [Fact]
    public void PreviewDpiShowsWhyTheCdrImageCannotBePrinted()
    {
        var preview = new CdrPreview(CdrPreviewKind.Argb32, 200, 160, null, null, "riff:DISP");
        Assert.True(preview.EquivalentDpiAt(100) < 60, "200 像素铺 100mm 只有 50DPI 量级，必须一眼看出不能印");
    }

    // ---------- SVG → 模板 ----------

    private const string CdrExportedSvg = """
<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm" viewBox="0 0 100 80">
  <style>.lbl { font-size: 5pt; font-family: Arial; } .val { font-size: 7pt; font-family: Arial; }</style>
  <g id="Frame">
    <rect x="2" y="2" width="96" height="76" fill="none" stroke="#000000" stroke-width="0.4"/>
    <path d="M50 60 L54 68 L46 68 Z" fill="#000000"/>
  </g>
  <text class="lbl" x="4" y="12">合同号</text>
  <text class="val" id="pod" x="40" y="12">LOS ANGELES, USA</text>
  <text class="val" x="4" y="22">SD-2026-0831</text>
  <text class="lbl" x="4" y="72">MADE IN CHINA</text>
</svg>
""";

    private static string WriteTemp(string content, string extension = ".svg")
    {
        var path = Path.Combine(Path.GetTempPath(), $"labelgou-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void ImportPlanReadsCanvasSizeAndOffersEveryTextNode()
    {
        var path = WriteTemp(CdrExportedSvg);
        try
        {
            var plan = TemplateImporter.FromSvgFile(path);
            Assert.Equal(TemplateImportSource.Svg, plan.Source);
            Assert.Equal(100, plan.LabelWidthMm, 3);
            Assert.Equal(80, plan.LabelHeightMm, 3);
            Assert.Equal(4, plan.Texts.Count);
            Assert.True(plan.HasVectorBackground);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OnlyTextsThatMatchSampleDataArePrePromoted()
    {
        var path = WriteTemp(CdrExportedSvg);
        try
        {
            var plan = TemplateImporter.FromSvgFile(path);

            var pod = plan.Texts.Single(t => t.Content == "LOS ANGELES, USA");
            Assert.True(pod.Promote);
            Assert.Equal(MarkFieldKey.DestinationPort, pod.Field);
            Assert.True(pod.Confidence > 0.5);

            var contract = plan.Texts.Single(t => t.Content == "SD-2026-0831");
            Assert.True(contract.Promote);
            Assert.Equal(MarkFieldKey.ContractNo, contract.Field);

            // "合同号" 是字段名标签，绑上去会把固定文字变成死字段 → 不预勾
            var label = plan.Texts.Single(t => t.Content == "合同号");
            Assert.False(label.Promote);
            Assert.Contains("固定文字", label.Reason, StringComparison.Ordinal);

            // MADE IN CHINA 恰好也是 Origin 的样例值 → 这种就该预勾（报告里会写明依据）
            var origin = plan.Texts.Single(t => t.Content == "MADE IN CHINA");
            Assert.True(origin.Promote);
            Assert.Equal(MarkFieldKey.Origin, origin.Field);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PromotedTextIsLiftedOutOfTheBackgroundSoNothingIsPrintedTwice()
    {
        var path = WriteTemp(CdrExportedSvg);
        try
        {
            var plan = TemplateImporter.FromSvgFile(path);
            var background = plan.BuildBackgroundSvg();
            Assert.NotNull(background);
            Assert.DoesNotContain("LOS ANGELES", background!, StringComparison.Ordinal);
            Assert.DoesNotContain("SD-2026-0831", background!, StringComparison.Ordinal);
            // 没被提升的固定标签文字必须还在底图里，否则那块地方就空了
            Assert.Contains("合同号", background!, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuiltTemplatePutsWholeDraftInOneElementAndSurvivesValidation()
    {
        var path = WriteTemp(CdrExportedSvg);
        var store = new TemplateStore(MakeTempDir());
        try
        {
            var plan = TemplateImporter.FromSvgFile(path);
            var (template, issues) = plan.Build("客户 A 唛头", store);

            Assert.False(issues.HasError(), string.Join(" | ", issues.ErrorMessages()));
            Assert.Equal(100, template.WidthMm, 3);
            Assert.Equal(0, template.BorderMm);   // 底稿自带边框，不再套一圈

            var background = Assert.Single(template.Elements, e => e.Kind == ElementKind.Vector);
            Assert.False(string.IsNullOrWhiteSpace(background.ImagePath));
            Assert.True(File.Exists(Path.Combine(store.UserDirectory, background.ImagePath!)), "底图必须落进 assets\\ 并可用相对路径找到");

            var texts = template.Elements.Count(e => e.Kind == ElementKind.Text);
            Assert.Equal(plan.PromotedCount, texts);
            Assert.Contains(template.Elements.OfType<TemplateElement>(), e => e.Text == "{{ContractNo}}");
            Assert.True(template.SchemaVersion == LabelTemplate.CurrentSchemaVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ElementsFromADraftThatOverspillTheLabelAreClampedNotDropped()
    {
        // 底稿文字压在画布边上：收进来贴边，绝不能因为越界就被校验拒掉
        const string svg = """
<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm" viewBox="0 0 100 80">
  <text x="80" y="78" font-size="14pt">WALMART INC. / 深圳顺达贸易</text>
</svg>
""";
        var path = WriteTemp(svg);
        var store = new TemplateStore(MakeTempDir());
        try
        {
            var plan = TemplateImporter.FromSvgFile(path);
            var candidate = Assert.Single(plan.Texts);
            Assert.True(candidate.Promote);
            var (template, issues) = plan.Build("贴边测试", store);
            Assert.False(issues.HasError(), string.Join(" | ", issues.ErrorMessages()));
            var element = Assert.Single(template.Elements, e => e.Kind == ElementKind.Text);
            Assert.True(element.X + element.Width <= template.WidthMm + TemplateValidator.ToleranceMm);
            Assert.True(element.Y + element.Height <= template.HeightMm + TemplateValidator.ToleranceMm);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TooManyCandidatesKeepTheMostLikelyOnes()
    {
        var body = new StringBuilder();
        // 造 70 段都像可变字段的文字（远超 60 上限）
        for (var i = 0; i < 70; i++)
            body.Append($"<text x=\"{1 + i % 90}\" y=\"{5 + i % 70}\" font-size=\"6\">POD-{i:00} LOS ANGELES, USA</text>");
        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100mm\" height=\"80mm\" viewBox=\"0 0 100 80\">{body}</svg>";

        var path = WriteTemp(svg);
        try
        {
            var plan = TemplateImporter.FromSvgFile(path);
            Assert.True(plan.PromotedCount <= TemplateImportPlan.PromotableLimit,
                $"预勾数 {plan.PromotedCount} 必须不超过 {TemplateImportPlan.PromotableLimit}，否则存库必被 80 上限拦下");
            Assert.Contains(plan.Issues, i => i.Message.Contains("置信度", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------- 参考底图：不进打印 ----------

    private static TemplateElement ReferenceElement(string asset) => new()
    {
        Kind = ElementKind.Image,
        ImagePath = asset,
        X = 0, Y = 0, Width = 100, Height = 80,
        ReferenceOnly = true,
    };

    /// <summary>一个真存在的图文件（LayoutEngine 只把文件存在的图片产进版面，绝对路径最不容易踩到机器差异）。</summary>
    private static string ExistingAsset()
    {
        var path = Path.Combine(Path.GetTempPath(), $"labelgou-ref-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3 });
        return path;
    }

    [Fact]
    public void ReferenceElementsAreAbsentFromPrintSnapshotAndPresentOnScreen()
    {
        var asset = ExistingAsset();
        try
        {
            var template = new LabelTemplate { Name = "参考测试", WidthMm = 100, HeightMm = 80, BorderMm = 0.5 };
            template.Elements.Add(ReferenceElement(asset));
            template.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = "{{ContractNo}}", X = 5, Y = 5, Width = 40, Height = 6 });

            var record = SampleRecords.StandardSample();
            var print = LayoutEngine.Build(template, record, new LayoutContext(1, 1));
            Assert.DoesNotContain(print.Items, i => i is ImageItem);
            Assert.Contains(print.Items, i => i is TextItem);   // 正文该印的还在

            var screen = LayoutEngine.Build(template, record, new LayoutContext(1, 1, IncludeReference: true));
            var image = Assert.Single(screen.Items.OfType<ImageItem>());
            Assert.True(image.ReferenceOnly);
            Assert.Equal(asset, image.AbsolutePath);
        }
        finally
        {
            File.Delete(asset);
        }
    }

    [Fact]
    public void AReferenceOnlyTemplateWarnsThatItWouldPrintBlank()
    {
        var template = new LabelTemplate { Name = "只有参考图", WidthMm = 100, HeightMm = 80, BorderMm = 0 };
        template.Elements.Add(ReferenceElement(ExistingAsset()));
        var issues = TemplateValidator.Validate(template);
        Assert.Contains(issues, i => i.Severity == IssueLevel.Warning && i.Message.Contains("空白", StringComparison.Ordinal));
    }

    [Fact]
    public void VectorElementWithoutFileIsAWarningNotAnError()
    {
        var template = new LabelTemplate { Name = "缺底图", WidthMm = 100, HeightMm = 80 };
        template.Elements.Add(new TemplateElement { Kind = ElementKind.Vector, X = 0, Y = 0, Width = 100, Height = 80 });
        var issues = TemplateValidator.Validate(template);
        Assert.False(issues.HasError());
        Assert.Contains(issues, i => i.Severity == IssueLevel.Warning && i.Message.Contains("矢量底图", StringComparison.Ordinal));
    }

    [Fact]
    public void NewerSchemaVersionIsRefused()
    {
        var template = new LabelTemplate { Name = "未来版本", WidthMm = 100, HeightMm = 80 };
        template.SchemaVersion = LabelTemplate.CurrentSchemaVersion + 1;
        var issues = TemplateValidator.Validate(template);
        Assert.True(issues.HasError());
        Assert.Contains(issues.ErrorMessages(), m => m.Contains("升级", StringComparison.Ordinal));
    }

    [Fact]
    public void V1TemplateFileStillLoads()
    {
        var dir = MakeTempDir();
        var store = new TemplateStore(dir);

        // 用存档自己的口径造一份 v1 文件（只把版本号改回去）：v1 里根本不会有新字段
        var original = new LabelTemplate { Name = "旧版模板", WidthMm = 100, HeightMm = 80 };
        original.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "{{ContractNo}}", X = 5, Y = 5, Width = 60, Height = 8, FontSizePt = 10,
        });
        var json = TemplateStore.ToJson(original)
            .Replace("\"schemaVersion\": 2", "\"schemaVersion\": 1", StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);

        var path = Path.Combine(dir, "legacy.json");
        File.WriteAllText(path, json);

        var (template, issues) = store.ReadFile(path);
        Assert.NotNull(template);
        Assert.False(issues.HasError(), string.Join(" | ", issues.ErrorMessages()));
        var element = Assert.Single(template!.Elements);
        Assert.False(element.ReferenceOnly);                          // 旧文件默认要打印
        Assert.Equal(ElementKind.Text, element.Kind);
        Assert.Equal("{{ContractNo}}", element.Text);
    }

    [Fact]
    public void AssetsAndTemplateShareOneRelativePathConvention()
    {
        var store = new TemplateStore(MakeTempDir());
        var relative = store.SaveAsset("客户 A 唛头.bg.svg", "<svg/>");
        Assert.Equal(Path.Combine("assets", "客户 A 唛头.bg.svg"), relative);
        Assert.True(File.Exists(Path.Combine(store.UserDirectory, relative)));

        store.DeleteAsset(relative);
        Assert.False(File.Exists(Path.Combine(store.UserDirectory, relative)));
    }

    [Fact]
    public void UnsafeAssetNamesAreWashedButChineseSurvives()
    {
        Assert.Equal("客户A唛头", TemplateStore.SafeAssetName("客户/A:唛头*"));
        Assert.Equal("asset", TemplateStore.SafeAssetName("   "));
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "labelgou-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
