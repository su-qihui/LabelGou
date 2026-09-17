using System.IO;
using System.Windows;
using LabelGou.App.ViewModels;
using LabelGou.Core.Barcodes;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 60 棒两条（用户 2026-09-14 两张截图）：
/// ①「等比缩放时可能出现图片中蜷缩的表现（放大没问题了）」——缩小后条码只占框中间一小条，
///    而且下面那串可读数字叠成糊的一串；②「文本或其他元素处于边缘时会触发——改成确定／跳过」——
///    存盘被"起点超出标签左上角"这类问题拦下时，只有"回去改"一条路。
/// </summary>
public class BarcodeShrinkAndSkipGateTests
{
    private static void OnStaThread(Action work)
        => LabelGou.App.Export.StaWorker.RunAsync((_, _) =>
        {
            work();
            return true;
        }, null, CancellationToken.None).GetAwaiter().GetResult();

    private static string TempFolder(string tag)
    {
        var folder = Path.Combine(Path.GetTempPath(), "labelgou-b60-" + tag + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // ---------- ① 缩小后：框＝码、数字跟着缩 ----------

    [Fact]
    public void ShrinkingTheBarcodeShrinksTheReadableDigitsWithIt() => OnStaThread(() =>
    {
        var small = Layout(40);     // 缩放 40 % → X 只剩 2 个像素 @300dpi
        var full = Layout(100);
        var legacy = Layout(scale: null);

        Assert.True(small.FontSizePt < full.FontSizePt,
            $"缩小条码后字号仍是 {small.FontSizePt:0.#}pt：两位数字会骑到一起（他截图那串糊字）");
        Assert.True(full.FontSizePt > 6 && full.FontSizePt < 8, $"100 % 时字号该照 CDR 的 7.215 个模块（≈6.9pt），实际 {full.FontSizePt:0.##}pt");
        Assert.Equal(8, legacy.FontSizePt, 3);                  // 老模板（没有尺寸参数）逐字不变
    });

    [Fact]
    public void DraggingTheCornerInwardLeavesNoGapBetweenTheBoxAndTheBars() => OnStaThread(() =>
    {
        var vm = new TemplateEditorViewModel(new LabelTemplate
        {
            Id = "user.b60", Name = "缩小看样", WidthMm = 140, HeightMm = 100, PaddingMm = 4, BorderMm = 0.5,
        }, new TemplateStore(TempFolder("shrink")));
        vm.SnapEnabled = false;
        vm.PreviewRecord = MarkRecord.Builder()
            .SetRow(1, "订单.xlsx").Set(MarkFieldKey.Consignee, "WALMART").SetCustom("col:条码", "4006381333931").Build();
        vm.AddBarcodeCommand.Execute(null);
        var bar = vm.Template.Elements.Single(e => e.Kind == ElementKind.Barcode);
        var (w0, h0) = (bar.Width, bar.Height);

        vm.BeginDrag(bar.X + bar.Width, bar.Y + bar.Height, 2.5);      // 右下角往回缩一半
        vm.DragTo(bar.X + bar.Width * 0.5, bar.Y + bar.Height * 0.5);
        vm.EndDrag();

        Assert.True(bar.Width < w0 * 0.75 && bar.Height < h0, $"没缩下来（{w0:0.#}×{h0:0.#} → {bar.Width:0.#}×{bar.Height:0.#}）");
        var item = vm.SampleLayout.Items.OfType<BarcodeItem>().Single();
        var enc = BarcodeEncoder.Encode("123456789", BarcodeSymbology.Code128);
        // 两头的留白只许是静区本身（Code128 走 CDR 的固定 3 mm；没有毫米声明的制式才按窄元素数算）。
        // 再多的那一截就是"框与码脱了钩"——他说的「蜷缩」。
        var quiet = enc.QuietZoneMm > 0 ? enc.QuietZoneMm : 10 * item.ModuleMm;
        var leftGap = item.Bars[0].X - bar.X;
        var last = item.Bars[^1];
        var rightGap = bar.X + bar.Width - (last.X + last.Width);
        Assert.True(leftGap <= quiet + 0.2 && rightGap <= quiet + 0.2,
            $"框宽 {bar.Width:0.###} mm：条左边多留 {leftGap - quiet:0.#} mm、右边多留 {rightGap - quiet:0.#} mm，码没铺满框");
    });

    private static BarcodeItem Layout(double? scale)
    {
        var template = new LabelTemplate { Name = "字号看样", WidthMm = 140, HeightMm = 100, BorderMm = 0 };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Barcode, Text = "{{col:条码}}", Symbology = BarcodeSymbology.Ean13,
            ShowBarcodeText = true, FontSizePt = 8, X = 4, Y = 4, Width = 60, Height = 30,
            BarcodeSize = scale is null ? null : new BarcodeSizing(ScalePercent: scale.Value),
        });
        var record = MarkRecord.Builder()
            .SetRow(1, "字号.xlsx").Set(MarkFieldKey.Consignee, "WALMART").SetCustom("col:条码", "4006381333931").Build();
        return LayoutEngine.Build(template, record, new LayoutContext(1, 1))
            .Items.OfType<BarcodeItem>().Single();
    }

    // ---------- ② 存盘闸门：确定／跳过 ----------

    /// <summary>
    /// 他截图那条：元素起点在纸外（Y = −7.7 mm）。
    /// <para>第 81 棒把这一格从文本换成矩形——文本的排版盒不再当越界判据（Core 量不了字），
    /// 而这里要钉的是<b>存盘那道「确定／跳过」闸</b>本身：有 Error 就得问，问完不跳过就不许存。</para>
    /// </summary>
    private static TemplateEditorViewModel EdgeCaseVm(string folder)
    {
        var template = new LabelTemplate
        {
            Id = "user.b60-" + Path.GetFileName(folder), Name = "越界看样", WidthMm = 100, HeightMm = 80, PaddingMm = 4, BorderMm = 0,
        };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Rect, X = 0, Y = -7.7, Width = 60, Height = 10,
        });
        return new TemplateEditorViewModel(template, new TemplateStore(folder));
    }

    [Fact]
    public void SayingNoToTheSkipDialogStillRefusesTheSave() => OnStaThread(() =>
    {
        var folder = TempFolder("no");
        var vm = EdgeCaseVm(folder);
        var asked = 0;
        vm.AskSkipIssues = issues => { asked = issues.Count; return false; };     // 「确定」＝回去改

        vm.SaveCommand.Execute(null);

        Assert.True(asked > 0, "越界没被拦下来问一句：那道闸没生效");
        Assert.Empty(Directory.GetFiles(folder, "*.json"));                        // 也没存进去
    });

    [Fact]
    public void SkippingStoresTheTemplateAndKeepsTheIssuesVisible() => OnStaThread(() =>
    {
        var folder = TempFolder("skip");
        var vm = EdgeCaseVm(folder);
        vm.AskSkipIssues = _ => true;                                              // 「跳过」＝先不管这些问题

        vm.SaveCommand.Execute(null);

        Assert.NotEmpty(Directory.GetFiles(folder, "*.json"));                     // 真的存下来了
        Assert.Contains("超出标签", string.Join(" ", vm.Issues), StringComparison.Ordinal);   // 而问题仍在清单里（降成提示，不是被抹平）
        Assert.Contains(vm.Issues, m => m.Contains("编辑器里选了「跳过」", StringComparison.Ordinal));   // 写明是谁让它过的
    });
}
