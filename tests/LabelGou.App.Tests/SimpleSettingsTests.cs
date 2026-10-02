using System;
using System.Threading;
using System.Windows;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 简洁版设置页（第 102 棒）：三档小调优的默认、夹取、落盘，以及那扇窗本身加载得动。
/// <para>口径与上面几棒一致：<strong>能算的账写成纯判据</strong>（<see cref="SimpleShellFlow"/>），
/// 窗口只钉"造得出来 + 初始态诚实"——量尺寸那类断言在没 Show 的窗上是假的（§五-182）。</para>
/// </summary>
public class SimpleSettingsTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    [Theory]
    [InlineData(0, 56)]        // 没记过 = 默认：旧状态文件缺这两格时一个像素都不变
    [InlineData(56, 56)]
    [InlineData(10, 24)]       // 太小 → 下限
    [InlineData(999, 120)]     // 太大 → 上限（超过 120 纸就缩到看不清字）
    [InlineData(-8, 56)]       // 负数按"没记过"处理，不许当 0 用
    public void 纸张留白认默认也夹得进范围(double stored, double expected)
        => Assert.Equal(expected, SimpleShellFlow.PaperMargin(stored));

    [Theory]
    [InlineData(0, 248)]
    [InlineData(260, 260)]
    [InlineData(100, 180)]
    [InlineData(4000, 320)]
    public void 海报墙卡片宽认默认也夹得进范围(double stored, double expected)
        => Assert.Equal(expected, SimpleShellFlow.PosterCardWidth(stored));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 坏数一律退回默认而不是写进状态文件(bool which)
    {
        // §五-183 同族：NaN / ∞ 从 UI 或旧文件里冒出来时，不许原样往下传
        Assert.Equal(56, SimpleShellFlow.PaperMargin(which ? double.NaN : double.PositiveInfinity));
        Assert.Equal(248, SimpleShellFlow.PosterCardWidth(which ? double.NaN : double.NegativeInfinity));
    }

    [Fact]
    public void 两档一起落盘再读回来还是那两个数()
    {
        var store = TestEnvironment.NewTempUiStateStore();
        var vm = new MainViewModel(store);
        Assert.Equal((56d, 248d), (vm.LoadPaperMargin(), vm.LoadPosterCardWidth()));   // 缺字段 = 默认

        vm.SaveShellTweaks(80, 300);
        var again = new MainViewModel(store);
        Assert.Equal((80d, 300d), (again.LoadPaperMargin(), again.LoadPosterCardWidth()));
    }

    [Fact]
    public void 设置窗造得出来并且把存过的数摆在控件上()
        => OnSta(() =>
        {
            var store = TestEnvironment.NewTempUiStateStore();
            var vm = new MainViewModel(store);
            var first = new SimpleSettingsWindow(vm, dark: false);
            Assert.Equal(56, first.PaperMargin);           // 没记过 → 出厂那档
            Assert.Equal(248, first.PosterCardWidth);
            Assert.False(first.Accepted);                  // 刚造出来不许自认"他点了好"

            vm.SaveShellTweaks(96, 200);
            var second = new SimpleSettingsWindow(vm, dark: true);
            Assert.Equal(96, second.PaperMargin);
            Assert.Equal(200, second.PosterCardWidth);
            return 0;
        });

    /// <summary>
    /// 第 103 棒：「AI 提示语」那扇窗造得出来，并且把<strong>当前实际在用的那一版</strong>摆在编辑框里
    /// （不是永远摆出厂版——那样他改完重开一看还是老话，就会以为没存上）。
    /// </summary>
    [Fact]
    public void 提示语窗造得出来并显示正在用的那一版()
        => OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            var first = new AiPromptWindow(vm, dark: false);
            Assert.False(first.Changed);                       // 没动过就不许自认"改过了"
            Assert.Equal(8, first.SectionCount);               // 八段全在清单上，一段不少

            var section = LabelGou.Core.Recognition.PromptCatalog.All[3];
            var mine = "我自己写的那段：itemno-tail、qty-column、row-keep、template-source、header-row、fixed-value、column-meaning";
            vm.PromptOverrides.Save(section.Key, mine);
            var second = new AiPromptWindow(vm, dark: true);
            Assert.Equal(mine, second.TextOf(section.Key));
            // 没改过的那段照旧是出厂版（一段被改不该把别段也带走）
            Assert.Equal(LabelGou.Core.Recognition.PromptCatalog.DefaultOf(
                LabelGou.Core.Recognition.PromptKeys.System), second.TextOf(LabelGou.Core.Recognition.PromptKeys.System));
            return 0;
        });
}
