using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core;
using LabelGou.Core.Data;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// ① 步「这张表体检」面板的闭环钉子（导入层第 1 棒）。
/// <para>Core 那边证的是判据，这一层证的是<strong>那颗键真的把表改对了</strong>——§五-123 那类
/// 「点了报成功、其实什么都没改」正是从这一层漏出去的。夹具 = 金沐那张表的形状（D 列写着
/// 「张数等于件数」、F 列抄着标签、货号带 *144 重复尾巴）。</para>
/// <para>用户 2026-09-13 实测后定的三条口径都在这儿钉住：面板一句要短（凭什么进 ToolTip）、
/// F 列那种表内文字模板不写进面板、执行完预览里要真看得到改好的表。</para>
/// </summary>
public sealed class ImportHealthPanelTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-health");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private string WriteJinMuCsv(string name)
    {
        var text = new StringBuilder("货号 ITEM NO:,件数 CTN,数量 QTY,一开四,,");
        text.AppendLine();
        string[] block = { "BOLAROM", "Item no：olu830-35", "QTY：144 pcs", "Ctns：5件" };
        for (var i = 0; i < 8; i++)
        {
            text.AppendLine($"olu830-{35 + i}*144,5,144,{(i == 0 ? "张数等于件数" : "")},,{(i < 4 ? block[i] : "")}");
        }
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static MainViewModel LoadOffline(string csv)
    {
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
        vm.Mode = RunMode.Offline;
        vm.LoadSource(csv, null);
        return vm;
    }

    /// <summary>取 ① 步预览表第一行的某一格。列 0 是行号，所以「D 列」是 4、「货号」是 1。</summary>
    private static string CellAt(MainViewModel vm, int column)
        => vm.PreviewTable!.Rows[0][column]?.ToString() ?? string.Empty;

    [Fact]
    public void 面板只摆短句_凭什么挪到ToolTip()
    {
        var csv = WriteJinMuCsv("short.csv");

        var probe = OnSta(() =>
        {
            var vm = LoadOffline(csv);
            return (shorts: vm.HealthRows.Select(r => r.Text).ToList(), detail: vm.HealthRows.First().Detail);
        });

        Assert.NotEmpty(probe.shorts);
        Assert.All(probe.shorts, s => Assert.True(s.Length <= 30, $"面板那句太长了：{s}"));
        Assert.DoesNotContain("——", probe.shorts);
        Assert.Contains("凭什么", probe.detail);   // 细节没丢，只是不占面板
    }

    [Fact]
    public void 表内文字模板那一列不写进面板()
    {
        var csv = WriteJinMuCsv("f.csv");

        var facts = OnSta(() => LoadOffline(csv).HealthRows.Select(r => r.Text).ToList());

        Assert.DoesNotContain(facts, f => f.Contains("F 列"));   // 用户：那是给 AI 排版用的，不用管不用写
    }

    [Fact]
    public void 执行后预览里D列真的等于B列_星号尾巴也没了()
    {
        var csv = WriteJinMuCsv("exec.csv");

        var probe = OnSta(() =>
        {
            var vm = LoadOffline(csv);
            var before = (d: CellAt(vm, 4), a: CellAt(vm, 1));
            vm.HealthFixAllCommand.Execute(null);
            return (
                before,
                after: (d: CellAt(vm, 4), a: CellAt(vm, 1)),
                rules: vm.CurrentChoice.ValueRules?.Count ?? 0,
                status: vm.StatusMessage);
        });

        Assert.Equal("张数等于件数", probe.before.d);               // 原来那一格装的是指令本身
        Assert.Equal("olu830-35*144", probe.before.a);
        Assert.Equal("5", probe.after.d);                       // 填进来了 = B 列的件数
        Assert.Equal("olu830-35", probe.after.a);               // * 号及后面删掉了
        Assert.True(probe.rules >= 2);
        Assert.Contains("一键修复", probe.status);
    }

    [Fact]
    public void 撤回这一步_预览回到改之前()
    {
        var csv = WriteJinMuCsv("undo.csv");

        var probe = OnSta(() =>
        {
            var vm = LoadOffline(csv);
            vm.HealthFixAllCommand.Execute(null);
            var fixedD = CellAt(vm, 4);
            vm.HealthUndoCommand.Execute(null);
            return (fixedD, back: CellAt(vm, 4),
                rules: vm.CurrentChoice.ValueRules?.Count ?? 0, status: vm.StatusMessage);
        });

        Assert.Equal("5", probe.fixedD);
        Assert.Equal("张数等于件数", probe.back);
        Assert.Equal(0, probe.rules);
        Assert.Contains("已撤回", probe.status);
    }

    [Fact]
    public void 逐条执行只落点的那一条()
    {
        var csv = WriteJinMuCsv("one.csv");

        var probe = OnSta(() =>
        {
            var vm = LoadOffline(csv);
            var row = vm.HealthRows.Single(r => r.Text.StartsWith("D 列"));
            vm.HealthFixOneCommand.Execute(row);
            return (
                d: CellAt(vm, 4),
                a: CellAt(vm, 1),
                stillFixable: vm.HealthRows.Count(r => r.CanFix));
        });

        Assert.Equal("5", probe.d);                             // 点的那条生效了
        Assert.Equal("olu830-35*144", probe.a);                 // 没点的那条不动
        Assert.True(probe.stillFixable > 0, "剩下的可修项要还在清单上，别点了就消失");
    }

    [Fact]
    public void 关掉一级校验开关_清单清空也不再扫()
    {
        var csv = WriteJinMuCsv("off.csv");

        var probe = OnSta(() =>
        {
            var vm = LoadOffline(csv);
            var on = vm.HealthRows.Count;
            vm.HealthCheckEnabled = false;
            vm.LoadSource(csv, null);
            var off = vm.HealthRows.Count;
            vm.HealthCheckEnabled = true;
            return (on, off, backOn: vm.HealthRows.Count);
        });

        Assert.True(probe.on > 0);
        Assert.Equal(0, probe.off);
        Assert.True(probe.backOn > 0, "开关拨回来必须重新扫出来，不能只清不建");
    }
}
