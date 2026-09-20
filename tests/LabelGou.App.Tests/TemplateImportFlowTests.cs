using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using LabelGou.App.ViewModels;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 「从底稿导入」这条操作链路：三态入口 → 窗口里的勾选与改绑 → 真的落库（模板 + assets）。
/// <para>
/// 与 M4 同一手法：<strong>真加载 XAML、真建窗口、真写模板目录</strong>，因为最容易出事的
/// 恰好是"绑定名写错一个字母"和"相对路径落在哪个目录"这类接缝，纯逻辑单测照不出来。
/// 窗口全程不 Show（没桌面会话也能跑），只量内容根。
/// </para>
/// </summary>
public class TemplateImportFlowTests
{
    private const string Artwork = """
<svg xmlns="http://www.w3.org/2000/svg" width="100mm" height="80mm" viewBox="0 0 100 80">
  <style>.lbl { font-size: 5pt; } .val { font-size: 7pt; }</style>
  <rect x="2" y="2" width="96" height="76" fill="none" stroke="#000000" stroke-width="0.4"/>
  <path d="M50 60 L54 68 L46 68 Z" fill="#000000"/>
  <text class="lbl" x="4" y="12">合同号</text>
  <text class="val" id="pod" x="40" y="12">LOS ANGELES, USA</text>
  <text class="val" x="4" y="22">SD-2026-0831</text>
</svg>
""";

    /// <summary>1×1 的合法 PNG，够小，能走 .cdr 的内嵌预览图那条路。</summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==");

    private static void OnStaThread(Action work)
        => LabelGou.App.Export.StaWorker
            .RunAsync<bool>((_, _) => { work(); return true; }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static string WithTemp(Action<string> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "labelgou-m5-ui-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string WriteSvg(string folder, string content = Artwork)
    {
        var path = Path.Combine(folder, "底稿.svg");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string WriteCdrWithThumbnail(string folder)
    {
        var path = Path.Combine(folder, "底稿.cdr");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("thumbnail.png");
            using var target = entry.Open();
            target.Write(OnePixelPng);
        }
        return path;
    }

    private static TemplateImportViewModel OpenViewModel(string path, string storeFolder)
    {
        var (vm, error) = TemplateImportViewModel.Open(path, new TemplateStore(storeFolder));
        Assert.True(vm is not null, "入口就该给出可读的失败原因：" + error);
        return vm!;
    }

    /// <summary>
    /// 直接按"预览参考底图"那条路建 VM（<c>.cdr</c> 的入口现在默认走逐对象，
    /// 而这条路本身还在、它的不变量还得有人守着，所以不经入口、直接给计划构造）。
    /// </summary>
    private static TemplateImportViewModel PreviewRouteViewModel(string cdrPath, string storeFolder)
    {
        var plan = TemplateImporter.FromCdrFile(cdrPath);
        Assert.True(plan.Source == TemplateImportSource.CdrPreview, "这条路该产 CdrPreview 计划");
        return new TemplateImportViewModel(plan, new TemplateStore(storeFolder));
    }

    /// <summary>
    /// 回归（2026-09-19 用户实测崩在此处）：构造函数先设宽度文字、后建 ImportCommand，
    /// setter 里 RaiseCanExecute 就撞 NullReferenceException。画布宽恰好等于兜底值 "100" 时
    /// setter 提前返回所以从没暴露——140×100 的 CDR 一进来就炸。这条钉住"宽度不是 100 也不许抛"。
    /// </summary>
    [Fact]
    public void APlanWhoseCanvasIsNot100MmWideDoesNotCrashTheWindow() => OnStaThread(() =>
    {
        WithTemp(folder =>
        {
            var plan = new TemplateImportPlan
            {
                Source = TemplateImportSource.Svg,
                SourcePath = Path.Combine(folder, "宽140.cdrx.json"),
                LabelWidthMm = 140,
                LabelHeightMm = 100,
            };
            var vm = new TemplateImportViewModel(plan, new TemplateStore(folder));
            Assert.Equal("140", vm.LabelWidthText);
            Assert.Equal("100", vm.LabelHeightText);
        });
    });

    // ---------- 入口分流 ----------

    [Fact]
    public void SvgRouteReadsEveryTextNodeAndKeepsTheArtwork() => OnStaThread(() =>
    {
        var folder = Path.GetTempPath();
        var svg = Path.Combine(folder, $"labelgou-{Guid.NewGuid():N}.svg");
        File.WriteAllText(svg, Artwork, new UTF8Encoding(false));
        try
        {
            var (vm, error) = TemplateImportViewModel.Open(svg, new TemplateStore(Path.Combine(folder, "无目录")));
            Assert.Null(error);
            Assert.NotNull(vm);
            Assert.Equal(3, vm!.Rows.Count);
            Assert.False(vm.IsCdrRoute);
            Assert.Contains(vm.SummaryLines, line => line.Contains("SVG 底稿", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(svg);
        }
    });

    [Fact]
    public void CdrRouteIsFlaggedAsReferenceOnly() => OnStaThread(() =>
    {
        WithTemp(folder =>
        {
            var vm = PreviewRouteViewModel(WriteCdrWithThumbnail(folder), folder);
            Assert.True(vm.IsCdrRoute);
            Assert.Empty(vm.Rows);
            Assert.Contains("CorelDRAW", vm.NoTextHint, StringComparison.Ordinal);
            Assert.True(vm.CanImport, "带预览图的 .cdr 该能导入：" + vm.StatusLine);
        });
    });

    [Fact]
    public void GarbageFileGetsAnErrorInsteadOfAnException() => OnStaThread(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"labelgou-{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, "这显然不是 SVG", new UTF8Encoding(false));
        try
        {
            var (vm, error) = TemplateImportViewModel.Open(path, new TemplateStore(Path.GetTempPath()));
            Assert.Null(vm);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
        finally
        {
            File.Delete(path);
        }
    });

    // ---------- 窗口与绑定 ----------

    [Fact]
    public void ImportWindowXamlLoadsAndBinds() => OnStaThread(() =>
    {
        WithTemp(folder =>
        {
            var vm = OpenViewModel(WriteSvg(folder), folder);
            var window = new TemplateImportWindow(vm);

            Assert.NotNull(window.Content);
            Assert.Same(vm, window.DataContext);

            // 没 Show 的 Window 尺寸由 HWND 驱动，量不出来；直接量内容根就能确认整棵树布好了
            var root = (FrameworkElement)window.Content!;
            root.Measure(new Size(960, 700));
            root.Arrange(new Rect(0, 0, 960, 700));
            root.UpdateLayout();

            Assert.Same(vm, window.CandidateGrid.DataContext);
            Assert.Equal(3, window.CandidateGrid.Items.Count);
            Assert.True(window.CandidateGrid.RenderSize.Height > 20, "候选表没拿到尺寸");
            window.Close();
        });
    });

    [Fact]
    public void TickingARowMovesThatLineOutOfTheArtwork() => OnStaThread(() =>
    {
        WithTemp(folder =>
        {
            var vm = OpenViewModel(WriteSvg(folder), folder);
            var pod = vm.Rows.Single(r => r.Content == "LOS ANGELES, USA");
            Assert.True(pod.Promote, "样例数据的值命中了到达港字段，应当预勾");
            Assert.NotNull(pod.SelectedField);

            var withPod = vm.Rows.Count(r => r.Promote);
            pod.Promote = false;
            Assert.Equal(withPod - 1, vm.Rows.Count(r => r.Promote));

            pod.Promote = true;
            Assert.Equal(withPod, vm.Rows.Count(r => r.Promote));
        });
    });

    [Fact]
    public void PickingAFieldRewritesTheElementTextAsAToken() => OnStaThread(() =>
    {
        WithTemp(folder =>
        {
            var store = new TemplateStore(folder);
            var vm = OpenViewModel(WriteSvg(folder), folder);
            var label = vm.Rows.Single(r => r.Content == "合同号");
            Assert.False(label.Promote, "字段名标签是固定文字，不该预勾（定案 D8 信号②）");

            // 把自动预勾的那两段先收回去，才能看清楚"人手动改绑"到底写了什么
            foreach (var row in vm.Rows) row.Promote = false;
            label.Promote = true;
            label.SelectedField = vm.FieldOptions.First(o => o.Value?.ToString() == "ContractNo");

            vm.TemplateName = "改绑测试";
            LabelTemplate? saved = null;
            vm.Saved += template => saved = template;
            vm.ImportCommand.Execute(null);

            Assert.NotNull(saved);
            var element = saved!.Elements.Single(e => e.Kind == ElementKind.Text);
            Assert.Equal("{{ContractNo}}", element.Text);
        });
    });

    // ---------- 真落库 ----------

    [Fact]
    public void ImportWritesTemplatePlusVectorAssetAndSelectsIt() => OnStaThread(() =>
    {
        WithTemp(folder =>
        {
            var store = new TemplateStore(folder);
            var vm = OpenViewModel(WriteSvg(folder), folder);
            vm.TemplateName = "底稿导入测试";

            LabelTemplate? saved = null;
            vm.Saved += template => saved = template;
            vm.ImportCommand.Execute(null);

            Assert.NotNull(saved);
            Assert.Equal(string.Empty, vm.ErrorText);

            // 底图占 1 个元素位，提升的文字各占 1 个（定案 D5）
            var template = saved!;
            Assert.Equal(0, template.BorderMm);
            Assert.Equal(LabelTemplate.CurrentSchemaVersion, template.SchemaVersion);
            var vector = Assert.Single(template.Elements, e => e.Kind == ElementKind.Vector);
            var texts = template.Elements.Count(e => e.Kind == ElementKind.Text);
            Assert.InRange(texts, 1, 3);
            Assert.True(vector.Width <= template.WidthMm + 0.001);

            // 资产落进 assets\，模板里存的是相对路径（换机器、导 JSON 都还能认）
            Assert.StartsWith(TemplateStore.AssetFolderName + Path.DirectorySeparatorChar, vector.ImagePath);
            var asset = Path.Combine(folder, vector.ImagePath!);
            Assert.True(File.Exists(asset), "底图 SVG 没写进模板目录");
            Assert.Contains("<svg", File.ReadAllText(asset), StringComparison.Ordinal);

            // 存进库后能原样读回来
            var reread = store.GetById(template.Id);
            Assert.NotNull(reread);
            Assert.Equal(template.Elements.Count, reread!.Elements.Count);
        });
    });

    [Fact]
    public void CdrReferenceImageLandsInAssetsAndStaysUnprintable() => OnStaThread(() =>
    {
        WithTemp(folder =>
        {
            var store = new TemplateStore(folder);
            var vm = PreviewRouteViewModel(WriteCdrWithThumbnail(folder), folder);
            vm.TemplateName = "参考图测试";

            LabelTemplate? saved = null;
            vm.Saved += template => saved = template;
            vm.ImportCommand.Execute(null);

            Assert.NotNull(saved);
            var reference = Assert.Single(saved!.Elements);
            Assert.Equal(ElementKind.Image, reference.Kind);
            Assert.True(reference.ReferenceOnly, "从 .cdr 抠出来的预览图绝不能上纸");
            Assert.True(File.Exists(Path.Combine(folder, reference.ImagePath!)));
        });
    });

    [Fact]
    public void BadInputsBlockTheImportAndSayWhy() => OnStaThread(() =>
    {
        WithTemp(folder =>
        {
            var vm = OpenViewModel(WriteSvg(folder), folder);
            Assert.True(vm.CanImport, "刚读到的底稿本该能导入：" + vm.StatusLine);

            vm.LabelWidthText = "不填";
            Assert.False(vm.CanImport);
            Assert.False(vm.ImportCommand.CanExecute(null));
            Assert.Contains("数字", vm.StatusLine, StringComparison.Ordinal);

            vm.LabelWidthText = "5000";
            Assert.False(vm.CanImport);
            Assert.Contains("600", vm.StatusLine, StringComparison.Ordinal);

            vm.LabelWidthText = "100";
            vm.TemplateName = "   ";
            Assert.False(vm.CanImport);
            Assert.Contains("名字", vm.StatusLine, StringComparison.Ordinal);
        });
    });

    [Fact]
    public void PromoteAllAndNoneRoundTrip() => OnStaThread(() =>
    {
        WithTemp(folder =>
        {
            var vm = OpenViewModel(WriteSvg(folder), folder);
            vm.PromoteNoneCommand.Execute(null);
            Assert.DoesNotContain(vm.Rows, r => r.Promote);

            vm.PromoteAllCommand.Execute(null);
            Assert.All(vm.Rows, r => Assert.True(r.Promote));
        });
    });
}
