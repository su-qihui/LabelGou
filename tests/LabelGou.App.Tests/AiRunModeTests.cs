using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core;
using LabelGou.Core.Marks;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 「运行模式」在界面那一侧的钉子（第 30 棒）。
/// <para>用户 2026-09-10 开屏实测后当场质问：*"刚才打开的时候还是默认先绑定了——这个没做吗"*。
/// 答：第 29 棒确实只做了改动卡，导入后仍是老流程（自动连线直接生效）。这一棒补上：
/// **AI 模式导入后先不绑定**，交 AI 读懂整张表再由它给绑定方案、人逐条确认。</para>
/// <para>三条钉子缺一不可：① 没记过状态文件时**默认就是 AI 模式**（用户明确不要"首次问一次"）；
/// ② AI 模式导入后**一个字段都不连**；③ **离线模式照旧自动连接**——老的五步人工程一个字都不许变
/// （这条是防回归的，用户随时要能切回去干活）。</para>
/// <para>WPF 对象断言一律留 STA 线程内（§五-91）。</para>
/// </summary>
public sealed class AiRunModeTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-runmode");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    private string WriteCsv(string name)
    {
        var text = new StringBuilder("流水号,货号 ITEM NO:,件数 CTN").AppendLine();
        text.AppendLine("AJ1,olu830-35,5");
        text.AppendLine("AJ2,olu830-70,3");
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void 状态文件里没记过时默认就是AI模式()
    {
        var mode = OnSta(() => new MainViewModel(TestEnvironment.NewTempUiStateStore()).Mode);

        Assert.Equal(RunMode.Ai, mode);          // 用户 2026-09-10 明确选的默认，不是"首次问一次"
    }

    [Fact]
    public void AI模式导入后一个字段都不连_等AI给绑定方案()
    {
        var csv = WriteCsv("ai.csv");

        var probe = OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            vm.Mode = RunMode.Ai;
            vm.LoadSource(csv, null);
            return (mapped: vm.FieldRows.Count(r => r.Mapped), total: vm.FieldRows.Count, text: vm.StatusMessage);
        });

        Assert.Equal(0, probe.mapped);                        // 一个都不连——程序按表头猜的正是要换掉的东西
        Assert.True(probe.total > 0);                          // 字段清单本身照列
        Assert.Contains("先不绑定", probe.text);                 // 而且如实告诉人"在等 AI"
    }

    [Fact]
    public void 离线模式导入后照旧自动连接_老流程一个字没变()
    {
        var csv = WriteCsv("offline.csv");

        var mapped = OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            vm.Mode = RunMode.Offline;
            vm.LoadSource(csv, null);
            return vm.FieldRows.Count(r => r.Mapped);
        });

        Assert.True(mapped > 0, "离线模式必须还是老的自动连线；这条是防回归的");
    }

    [Fact]
    public void 模式记进状态文件_下次启动还是它()
    {
        var store = TestEnvironment.NewTempUiStateStore();

        OnSta(() => { new MainViewModel(store).Mode = RunMode.Offline; return 0; });

        Assert.Equal(RunMode.Offline, OnSta(() => new MainViewModel(store).Mode));
    }

    [Fact]
    public void 逐条绑定时只动那一个字段()
    {
        var csv = WriteCsv("bind.csv");

        var probe = OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            vm.Mode = RunMode.Ai;
            vm.LoadSource(csv, null);
            var before = vm.FieldRows.Count(r => r.Mapped);

            var (ok, message) = vm.BindField(MarkFieldKey.ItemNo, 1);   // 第 2 列 = 货号 ITEM NO:
            var item = vm.FieldRows.First(r => r.FieldKey == nameof(MarkFieldKey.ItemNo));
            return (ok, message, before, item.ColumnIndex, mapped: vm.FieldRows.Count(r => r.Mapped));
        });

        Assert.True(probe.ok, probe.message);
        Assert.Equal(0, probe.before);
        Assert.Equal(1, probe.ColumnIndex);
        Assert.Equal(1, probe.mapped);                        // 只多绑了这一个字段，别的没跟着动
    }
}
