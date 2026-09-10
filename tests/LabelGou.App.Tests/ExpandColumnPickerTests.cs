using System.IO;
using System.Text;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core.Numbering;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// ④ 步「按哪一列数张数」这条链：表里的列名要能选到、选了要真的进规则、换表要回退。
/// <para>用户 2026-09-08：「张数一般表格里会有一列写的（例：表格写打印 5 张，那对应的那张就要排出
/// 5 张整张纸，不是一个标签）」。Core 侧的取数与报警由 <c>ExpandCountColumnTests</c> 守，
/// 这里守的是界面到规则那一段——它此前根本没有入口。</para>
/// </summary>
public sealed class ExpandColumnPickerTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-expand-col");

    /// <summary>MainViewModel 会碰 WPF 类型，必须在 STA 线程上造。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    /// <summary>件数都写 1，另开一列「打印张数」写 5 / 3 —— 只有选得到那一列才分得清两种结果。</summary>
    private string WriteSheetCsv(string name, string countHeader = "打印张数")
    {
        var path = Path.Combine(_dir, name);
        var text = string.Join(",", "货号 ITEM NO:", "件数 CTN", countHeader) + Environment.NewLine +
                   "olu830-35,1,5" + Environment.NewLine +
                   "olu830-70,1,3" + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private MainViewModel LoadInto(string csv, NumberingMode mode = NumberingMode.ExpandByCartonTotal)
    {
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
        vm.LoadSource(csv, null);
        vm.Sheet.Mode = mode;
        return vm;
    }

    [Fact]
    public void 下拉列出表里的每一列且第一项是不选()
    {
        var (count, firstValue, hasColumn) = OnSta(() =>
        {
            var vm = LoadInto(WriteSheetCsv("张数.csv"));
            return (
                vm.Sheet.ExpandColumnOptions.Count,
                vm.Sheet.ExpandColumnOptions[0].Value,
                vm.Sheet.ExpandColumnOptions.Any(o => o.Value == "打印张数"));
        });

        Assert.Equal(4, count);                     // 不选 + 三个表头列
        Assert.Null(firstValue);                    // 第一项 = 不选，不替用户改行为
        Assert.True(hasColumn);
    }

    [Fact]
    public void 选了哪一列就按那一列出张数()
    {
        var (labels, ruleColumn) = OnSta(() =>
        {
            var vm = LoadInto(WriteSheetCsv("张数.csv"));
            var option = vm.Sheet.ExpandColumnOptions.First(o => o.Value == "打印张数");
            vm.Sheet.SelectedExpandColumn = option;
            return (vm.Sheet.Labels.Count, vm.Sheet.BuildRule().ExpandCountColumn);
        });

        Assert.Equal("打印张数", ruleColumn);
        Assert.Equal(8, labels);                    // 5 + 3，不是件数那一列的 1 + 1
    }

    [Fact]
    public void 不选列时仍按连接好的件数出()
    {
        var labels = OnSta(() =>
        {
            var vm = LoadInto(WriteSheetCsv("张数.csv"));
            return vm.Sheet.Labels.Count;
        });

        Assert.Equal(2, labels);                    // 老行为：没选列就照 ExpandCountField 走
    }

    [Fact]
    public void 换表后原来选的列没了就退回不选()
    {
        var (before, after) = OnSta(() =>
        {
            var vm = LoadInto(WriteSheetCsv("张数.csv"));
            vm.Sheet.SelectedExpandColumn = vm.Sheet.ExpandColumnOptions.First(o => o.Value == "打印张数");
            var picked = vm.Sheet.BuildRule().ExpandCountColumn;

            // 换一张没有那一列的表：不能继续拿着不存在的列名算，也不能静默按 1 张
            vm.LoadSource(WriteSheetCsv("另一张.csv", countHeader: "每箱pcs"), null);
            return (picked, vm.Sheet.BuildRule().ExpandCountColumn);
        });

        Assert.Equal("打印张数", before);
        Assert.Null(after);
    }

    [Fact]
    public void 选中的列已经连上字段时也按那一列出张数()
    {
        // 真表上用户会选的那一列通常已经连好了字段（金沐的「件数 CTN」），
        // 那时表里不会另存一份 col: 键 —— 只查列名就会对着明明存在的列报「没有值」并且一行只出一张。
        var (labels, warnings) = OnSta(() =>
        {
            var path = Path.Combine(_dir, "件数当张数.csv");
            var text = string.Join(",", "货号 ITEM NO:", "件数 CTN", "数量 QTY") + Environment.NewLine +
                       "olu830-35,5,144" + Environment.NewLine +
                       "olu830-70,3,96" + Environment.NewLine;
            File.WriteAllText(path, text, new UTF8Encoding(true));

            var vm = LoadInto(path);
            vm.Sheet.SelectedExpandColumn = vm.Sheet.ExpandColumnOptions.First(o => o.Value != null && o.Value.Contains("件数"));
            return (vm.Sheet.Labels.Count, vm.IssueLines.Count(l => l.Contains("没有值")));
        });

        Assert.Equal(8, labels);        // 5 + 3
        Assert.Equal(0, warnings);      // 一条假告警都不该有
    }

    [Fact]
    public void 只有展开档才让这个下拉可用()
    {
        var (keepData, expand) = OnSta(() =>
        {
            var vm = LoadInto(WriteSheetCsv("张数.csv"), NumberingMode.KeepData);
            var a = vm.Sheet.IsExpandMode;
            vm.Sheet.Mode = NumberingMode.ExpandByCartonTotal;
            return (a, vm.Sheet.IsExpandMode);
        });

        Assert.False(keepData);
        Assert.True(expand);
    }

    [Fact]
    public void AI落地张数列_一步把档与列都设好_真按那一列展开()
    {
        // 用户实测问题 #3：「AI 问了是否将 x 列设为张数、答了是，结果仍是模版那一张。」
        // 根因最后一环：ApplyAiProposal 要把 Readout.QtyColumn 接到这里——不只是拼那五行摘要，
        // 得真把拼版切到「按张数展开」并选中那一列，预览才会一行出 5 张整张纸而不是一行一张。
        var (labels, mode, ruleColumn, msg) = OnSta(() =>
        {
            var vm = LoadInto(WriteSheetCsv("张数.csv"), NumberingMode.KeepData);   // 故意从默认档起步
            var m = vm.Sheet.ApplyQtyColumn("打印张数");
            return (vm.Sheet.Labels.Count, vm.Sheet.Mode, vm.Sheet.BuildRule().ExpandCountColumn, m);
        });

        Assert.StartsWith("已按", msg);
        Assert.Equal(NumberingMode.ExpandByCartonTotal, mode);   // 档被这一步切过来了
        Assert.Equal("打印张数", ruleColumn);
        Assert.Equal(8, labels);                                  // 5 + 3，不是一行一张的 2
    }

    [Fact]
    public void AI落地张数列_表里没那一列就如实说没接上_不改档不猜列()
    {
        var (labels, mode, msg) = OnSta(() =>
        {
            var vm = LoadInto(WriteSheetCsv("张数.csv"), NumberingMode.KeepData);
            var m = vm.Sheet.ApplyQtyColumn("根本没有这一列");
            return (vm.Sheet.Labels.Count, vm.Sheet.Mode, m);
        });

        Assert.Contains("没接上", msg);
        Assert.Equal(NumberingMode.KeepData, mode);   // 接不上就不动档
        Assert.Equal(2, labels);                       // 仍是一行一张
    }
}
