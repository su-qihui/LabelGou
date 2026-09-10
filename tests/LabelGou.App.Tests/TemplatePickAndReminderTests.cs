using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 9 棒批次三：导入后「自动挑模板」与「缺值提醒」这两件事的口径。
/// <para>它们都是用户会不会说「改了没变化」的直接来源：挑错模板 = 眼前永远是他圈过的那四格粗黑框；
/// 提醒钉死在一次操作的字符串里 = 换了模板还挂着上一套模板的缺项。</para>
/// <para>界面状态一律落临时目录，不写用户的 <c>%APPDATA%</c>（§五-48）。</para>
/// </summary>
public sealed class TemplatePickAndReminderTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-pick");

    /// <summary>MainViewModel 会碰 WPF 类型，必须在 STA 线程上造。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    private string WriteCsv(string name, params string[] headers)
    {
        var path = Path.Combine(_dir, name);
        var text = string.Join(",", headers) + Environment.NewLine +
                   "olu830-35,5,144" + Environment.NewLine +
                   "olu830-70,3,96" + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private static string? RememberedTemplate(UiStateStore store) => store.Load().TemplateId;

    [Fact]
    public void 自动接手按命中率挑模板而不是按命中数()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        store.Save(new UiState { TemplateId = BuiltInTemplates.IdStandard });
        var csv = WriteCsv("三列表.csv", "货号 ITEM NO:", "件数 CTN", "数量 QTY");

        var picked = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            Assert.Equal(BuiltInTemplates.IdStandard, vm.SelectedTemplate?.Id);   // 起点是用户记的那套九字段
            vm.LoadSource(csv, null);
            return vm.SelectedTemplate?.Id;
        });

        // 九字段的标准箱唛只填得上 4/9，行式四行填得上 3/4：
        // 上一版比的是命中数绝对值，字段多的永远赢，于是用户圈过的那四格粗黑框一直压着真样张那一套。
        Assert.Equal(BuiltInTemplates.IdRowsFour, picked);
    }

    [Fact]
    public void 一个字段都没连上时状态栏不说已自动连接()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        // TOP 那张表实测就是这个形状：首行就是数据、列名全是数字，别名一个也认不出。
        var csv = WriteCsv("无表头.csv", "11150588", "b5006*16", "1");

        var status = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            vm.LoadSource(csv, null);
            vm.AutoSuggestCommand.Execute(null);
            return vm.StatusMessage;
        });

        // 上一版这里无条件报「已按表头别名重新自动连接」，一个都没连上也是这句（用户拿截图问为什么骗人）。
        Assert.Contains("一个字段都没连上", status);
        Assert.DoesNotContain("已按表头别名重新自动连接", status);
    }

    [Fact]
    public void 连上了就把真数报出来而不是给一句空话()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        var csv = WriteCsv("有表头.csv", "货号 ITEM NO:", "件数 CTN", "数量 QTY");

        var status = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            vm.LoadSource(csv, null);
            vm.AutoSuggestCommand.Execute(null);
            return vm.StatusMessage;
        });

        Assert.Matches("连上 \\d+ 个字段", status);
        Assert.DoesNotContain("一个字段都没连上", status);
    }

    [Fact]
    public void 自动接手不写盘_手工换模板才写盘()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        store.Save(new UiState { TemplateId = BuiltInTemplates.IdStandard });
        var csv = WriteCsv("三列表.csv", "货号 ITEM NO:", "件数 CTN", "数量 QTY");

        var after = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            vm.LoadSource(csv, null);                       // 自动接手换成行式四行
            var autoSwitched = vm.SelectedTemplate?.Id;
            vm.SelectedTemplate = vm.TemplateOptions.First(t => t.Id == BuiltInTemplates.IdRowsBigTwo);
            return (Auto: autoSwitched, Remembered: RememberedTemplate(store));
        });

        Assert.Equal(BuiltInTemplates.IdRowsFour, after.Auto);
        // 自动那一次不算用户的选择：写盘就会把他手工记的模板顶掉，下一张表进来又按数据说话
        Assert.Equal(BuiltInTemplates.IdRowsBigTwo, after.Remembered);
    }

    [Fact]
    public void 启动兜底不写盘_记的模板被删时状态文件不被顶掉()
    {
        // 第 23 棒：记的那个模板不存在时，兜底选中的行式四行曾把状态文件里的旧记录静默顶掉——
        // 用户重启后「上次选过什么」已经无从对起。
        var store = TestEnvironment.NewTempUiStateStore();
        store.Save(new UiState { TemplateId = "user.gone-template" });   // 已被删掉的用户模板

        var after = OnSta(() =>
        {
            var vm = new MainViewModel(store);                           // 兜底选中内置行式四行
            return vm.SelectedTemplate?.Id;
        });

        Assert.Equal(BuiltInTemplates.IdRowsFour, after);               // 界面还是要兜底的
        Assert.Equal("user.gone-template", RememberedTemplate(store));  // 但写盘不许发生
    }

    [Fact]
    public void 数据代数_换文件换模板应用映射都会自增()
    {
        // AI 面板靠它判「等待期间数据换过没」：不等就作废那轮结果(第 23 棒)。
        var store = TestEnvironment.NewTempUiStateStore();
        var csv = WriteCsv("三列表.csv", "货号 ITEM NO:", "件数 CTN", "数量 QTY");
        var csv2 = WriteCsv("另一张表.csv", "客户", "毛重 KGS");

        var gens = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            var atStart = vm.DataGeneration;
            vm.LoadSource(csv, null);
            var afterLoad = vm.DataGeneration;
            vm.ApplyMappingCommand.Execute(null);
            var afterMap = vm.DataGeneration;
            vm.SelectedTemplate = vm.TemplateOptions.First(t => t.Id == BuiltInTemplates.IdRowsBigTwo);
            var afterTemplate = vm.DataGeneration;
            vm.LoadSource(csv2, null);
            return (atStart, afterLoad, afterMap, afterTemplate, vm.DataGeneration);
        });

        Assert.True(gens.afterLoad > gens.atStart);
        Assert.True(gens.afterMap > gens.afterLoad);
        Assert.True(gens.afterTemplate > gens.afterMap);
        Assert.True(gens.Item5 > gens.afterTemplate);
    }

    [Fact]
    public void 状态栏说的是填了几成而不是几个字段()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        store.Save(new UiState { TemplateId = BuiltInTemplates.IdStandard });
        var csv = WriteCsv("三列表.csv", "货号 ITEM NO:", "件数 CTN", "数量 QTY");

        var status = OnSta<string>(() =>
        {
            var vm = new MainViewModel(store);
            vm.LoadSource(csv, null);
            return vm.StatusMessage;
        });

        Assert.Contains("%", status, StringComparison.Ordinal);
        Assert.Contains("已自动改用", status, StringComparison.Ordinal);
    }

    [Fact]
    public void 缺值提醒在提示区里_补上固定值就自己消失()
    {
        var csv = WriteCsv("三列表.csv", "货号 ITEM NO:", "件数 CTN", "数量 QTY");

        OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            vm.LoadSource(csv, null);
            Assert.Equal(BuiltInTemplates.IdRowsFour, vm.SelectedTemplate?.Id);

            // 提醒不再拼进状态栏那一句（拼一次就会被后面的换模板钉成陈话），它在第 ② 步的提示区里
            Assert.Contains(vm.IssueLines, l => l.Contains("收货人", StringComparison.Ordinal));

            var rows = vm.BuildFixedValueRows();
            var consignee = rows.First(r => r.Definition.Key == MarkFieldKey.Consignee);
            consignee.Value = "BOLAROM";
            vm.ApplyFixedValues(rows);

            Assert.DoesNotContain(vm.IssueLines, l => l.Contains("收货人", StringComparison.Ordinal));
            return true;
        });
    }

    [Fact]
    public void 件号与总件数不算缺项()
    {
        // 那两格编号引擎每次都补，把它们列进「没值」是把人支去填一条白跑的固定值
        var csv = WriteCsv("两列表.csv", "货号 ITEM NO:", "数量 QTY");

        OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            vm.LoadSource(csv, null);
            vm.SelectedTemplate = vm.TemplateOptions.First(t => t.Id == BuiltInTemplates.IdStandard);

            Assert.DoesNotContain(vm.IssueLines, l => l.Contains("件号", StringComparison.Ordinal) && l.Contains("没值", StringComparison.Ordinal));
            Assert.DoesNotContain(vm.IssueLines, l => l.Contains("总件数", StringComparison.Ordinal) && l.Contains("没值", StringComparison.Ordinal));
            return true;
        });
    }

    [Fact]
    public void 直取列的量真没有时要说那一行整条不印()
    {
        // 表里没「件数」这一列 → 编号引擎补不出 col:本行箱数 → LayoutEngine 命中「变量全空整条隐藏」，
        // Ctns 那一行会无声消失。这句必须看得见，而不是让用户自己去猜为什么少一行。
        var csv = WriteCsv("两列表.csv", "货号 ITEM NO:", "数量 QTY");

        OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            vm.LoadSource(csv, null);
            vm.SelectedTemplate = vm.TemplateOptions.First(t => t.Id == BuiltInTemplates.IdRowsFour);

            Assert.Contains(vm.IssueLines, l =>
                l.Contains("本行箱数", StringComparison.Ordinal) && l.Contains("整条不印", StringComparison.Ordinal));
            return true;
        });
    }

    [Fact]
    public void 清空整批固定值后行对象自己会通知界面()
    {
        // FixedValueRow.Value 以前是个普通自动属性：「清空全部」把模型清空了、框里还顶着旧值（所见非所得）
        var rows = new List<MainViewModel.FixedValueRow>();
        OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            vm.LoadSource(WriteCsv("三列表.csv", "货号 ITEM NO:", "件数 CTN", "数量 QTY"), null);
            rows.AddRange(vm.BuildFixedValueRows());
            return true;
        });

        var row = rows.First(r => r.Definition.Key == MarkFieldKey.Consignee);
        var raised = 0;
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(row.Value)) raised++; };
        row.Value = "BOLAROM";
        row.Value = string.Empty;

        Assert.Equal(2, raised);
    }
}
