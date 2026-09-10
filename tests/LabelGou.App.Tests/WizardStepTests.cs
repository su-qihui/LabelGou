using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using LabelGou.App.Export;
using LabelGou.App.Mvvm;
using LabelGou.App.ViewModels;
using LabelGou.Core;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 五步向导的骨架。2026-09-07 用户两次反馈“界面还是原本的杂乱”，根因是左侧六个面板一次全摊在
/// 一条长滚动里 —— 新能力看不见、常用的纸规被整屏字段顶走。这几条把“一步只露一块、到头不许点出界”钉住。
/// <para>注意：这几条只钉住 VM 与转换器，没法钉住 MainWindow.xaml 里的面板拓扑（单测进程里造不出可用的
/// Application 资源，真造主窗口会报 StaticResource 找不到；已试过并回退）。那部分靠用户肉眼确认。</para>
/// <para>界面状态一律落临时目录，不写用户的 <c>%APPDATA%</c>（§五-48）。</para>
/// </summary>
public sealed class WizardStepTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-wizard");

    /// <summary>MainViewModel 会碰到 WPF 类型，必须在 STA 线程上造。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static MainViewModel NewVm() => new(TestEnvironment.NewTempUiStateStore());

    [Fact]
    public void 步骤条固定五步且带圈号()
    {
        OnSta(() =>
        {
            var vm = NewVm();
            Assert.Equal(5, vm.StepTitles.Count);
            Assert.Equal(new[] { "①", "②", "③", "④", "⑤" }, vm.StepTitles.Select(t => t[..1]).ToArray());
            Assert.True(vm.IsFirstStep);
            Assert.Contains("第 1 / 5 步", vm.StepHint);
            return true;
        });
    }

    [Fact]
    public void 步数出界时夹回两端且不越界()
    {
        OnSta(() =>
        {
            var vm = NewVm();

            vm.StepIndex = -9;
            Assert.Equal(0, vm.StepIndex);
            Assert.True(vm.IsFirstStep);

            vm.StepIndex = 99;
            Assert.Equal(4, vm.StepIndex);
            Assert.True(vm.IsLastStep);
            Assert.Contains("第 5 / 5 步", vm.StepHint);
            return true;
        });
    }

    [Fact]
    public void 走到头之后前进与后退命令自己变灰()
    {
        OnSta(() =>
        {
            var vm = NewVm();
            Assert.False(vm.PrevStepCommand.CanExecute(null));
            Assert.True(vm.NextStepCommand.CanExecute(null));

            for (var i = 0; i < 8; i++)
            {
                if (vm.NextStepCommand.CanExecute(null)) vm.NextStepCommand.Execute(null);
            }
            Assert.Equal(4, vm.StepIndex);
            Assert.False(vm.NextStepCommand.CanExecute(null));
            Assert.True(vm.PrevStepCommand.CanExecute(null));

            vm.PrevStepCommand.Execute(null);
            Assert.Equal(3, vm.StepIndex);
            return true;
        });
    }

    [Fact]
    public void 转换器只放行自己那一步的面板()
    {
        var converter = new StepIndexToVisibilityConverter();

        Assert.Equal(Visibility.Visible, converter.Convert(2, typeof(Visibility), "2", CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, converter.Convert(2, typeof(Visibility), "1", CultureInfo.InvariantCulture));
        // 参数缺失或不是数字时一律收起：宁可整页不露，也不让五块面板一起摊开（那就退回原来的杂乱）
        Assert.Equal(Visibility.Collapsed, converter.Convert(2, typeof(Visibility), null, CultureInfo.InvariantCulture));
        Assert.Equal(Visibility.Collapsed, converter.Convert(null, typeof(Visibility), "2", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void 导入表格后自动走到连接字段那一步()
    {
        var csv = Path.Combine(_dir, "唛头.csv");
        File.WriteAllText(csv,
            "货号,件数,数量\r\nolu830-35,5,144\r\nolu830-49,3,96\r\n", new UTF8Encoding(true));

        OnSta(() =>
        {
            var vm = NewVm();
            // 第 30 棒：默认运行模式已改成 **AI 模式**（导入后先不绑定，交 AI 读完整张表再由它绑）。
            // 这条测的是**离线模式**那条路——「自动连接已经在跑，结果必须露在眼前」，
            // 所以模式必须显式写出来，别让它跟着默认值漂（不写的话这条测的就不是自己要测的东西了）。
            vm.Mode = RunMode.Offline;
            Assert.Equal(0, vm.StepIndex);

            vm.LoadSource(csv, null);

            Assert.True(vm.HasData, "导入没成功，后面的步骤断言就没有意义");
            Assert.True(vm.RecordTotal > 0);
            // 自动连接已经在跑，但结果必须露在眼前：停在第 2 步（索引 1）
            Assert.Equal(1, vm.StepIndex);
            Assert.Contains("已连接", vm.StatusMessage);
            return true;
        });
    }

    [Fact]
    public void 用户手工跳过步时导入不会把他拽回去()
    {
        var csv = Path.Combine(_dir, "唛头2.csv");
        File.WriteAllText(csv, "货号,数量\r\nA1,144\r\n", new UTF8Encoding(true));

        OnSta(() =>
        {
            var vm = NewVm();
            vm.StepIndex = 4;     // 人已经滚到第 5 步了

            vm.LoadSource(csv, null);

            Assert.Equal(4, vm.StepIndex);
            return true;
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* 临时目录删不掉不影响结论 */ }
    }
}
