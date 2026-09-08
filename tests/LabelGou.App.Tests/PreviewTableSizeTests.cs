using System;
using System.IO;
using System.Linq;
using System.Threading;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// ① 步那张预览表的显示区大小（M7 第 16 棒，用户 2026-09-08：「这个表格显示区太小了可以选择扩大」）。
/// <para>钉的是两件事：<strong>默认档必须还是原来那个 170</strong>（谁都没被改变），
/// 以及<strong>选中的那一档要记住</strong>——否则"扩大"只活一次启动，跟第 8 棒那条「打开看没变化」是同一类毛病。</para>
/// <para>界面状态一律落临时目录，不写用户的 <c>%APPDATA%</c>（§五-48）。</para>
/// </summary>
public sealed class PreviewTableSizeTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-tablesize");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    private UiStateStore Store() => new(_dir);

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void 没记过时默认档就是以前那个170()
    {
        var height = OnSta(() => new MainViewModel(Store()).PreviewTableHeight);

        Assert.Equal(MainViewModel.DefaultPreviewTableHeight, height);
        Assert.Equal(170, height);
    }

    [Fact]
    public void 三档都能选而且都写着人话()
    {
        var options = OnSta(() => new MainViewModel(Store()).PreviewTableHeightOptions);

        Assert.Equal(3, options.Count);
        Assert.All(options, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
        Assert.Equal(new[] { 170d, 380d, 620d }, options.Select(o => o.Value).ToArray());
    }

    [Fact]
    public void 档位从矮到高排且最大的那档明显比旧值高()
    {
        var heights = MainViewModel.PreviewTableHeights.Select(p => p.Height).ToArray();

        Assert.Equal(heights.OrderBy(h => h).ToArray(), heights);
        Assert.True(heights[^1] >= heights[0] * 3, $"「大」只比旧值高 {heights[^1] / heights[0]:0.#} 倍，等于没扩大");
    }

    [Fact]
    public void 选大一号就把高度改掉并写进状态()
    {
        var snapshot = OnSta(() =>
        {
            var vm = new MainViewModel(Store());
            vm.SelectedPreviewTableHeight = vm.PreviewTableHeightOptions.Last();
            return (Height: vm.PreviewTableHeight, Message: vm.StatusMessage, Stored: Store().Load().PreviewTableHeight);
        });

        Assert.Equal(620, snapshot.Height);
        Assert.Equal(620, snapshot.Stored);            // 不写盘的话，下次启动又回到 170——那就是「改了没变化」
        Assert.Contains("表格", snapshot.Message);
        Assert.Equal(620, Store().Load().PreviewTableHeight);
    }

    [Fact]
    public void 下一次启动接回上次选的那档()
    {
        OnSta(() =>
        {
            var vm = new MainViewModel(Store());
            vm.SelectedPreviewTableHeight = vm.PreviewTableHeightOptions[1];
            return true;
        });

        var reopened = OnSta(() => new MainViewModel(Store()).PreviewTableHeight);

        Assert.Equal(380, reopened);
    }

    [Fact]
    public void 状态里记着一个不认识的数就退回默认而不是照用()
    {
        Store().Save(new UiState { PreviewTableHeight = 999 });

        var height = OnSta(() => new MainViewModel(Store()).PreviewTableHeight);

        // 999 像素会把整块顶出屏幕，用户反而找不到步骤条——认不出的数只能当候选，不能照单全收。
        Assert.Equal(MainViewModel.DefaultPreviewTableHeight, height);
    }

    [Fact]
    public void 旧状态文件没这个字段时行为零变化()
    {
        Store().Save(new UiState { TemplateId = "builtin.rows-four" });   // 模拟第 15 棒之前存的文件的形状

        var vm = OnSta<MainViewModel>(() => new MainViewModel(Store()));

        Assert.Equal(170, vm.PreviewTableHeight);
        Assert.NotNull(vm.SelectedPreviewTableHeight);
        Assert.Equal(170, vm.SelectedPreviewTableHeight!.Value);
    }
}
