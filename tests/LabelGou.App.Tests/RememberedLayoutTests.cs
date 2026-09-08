using System;
using System.IO;
using System.Linq;
using System.Threading;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using LabelGou.Core.Impos;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 「打开软件就能接着上次」：2026-09-07 用户反馈"打开看没变化"，正面修法不是让用户去下拉里翻新版式，
/// 而是软件记住他上次用的那套模板与纸规。这三条把这件事钉死。
/// <para>状态一律落到临时目录，不写用户的 <c>%APPDATA%</c>（§五-48 那个坑的同类）。</para>
/// </summary>
public sealed class RememberedLayoutTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "labelgou-remember-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>MainViewModel 会碰到 WPF 类型，必须在 STA 线程上造。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void 上次用的模板与纸规在下次启动时自动选中()
    {
        var store = new UiStateStore(_dir);
        store.Save(new UiState
        {
            TemplateId = BuiltInTemplates.IdRowsFour,
            SheetSpecId = BuiltInSheetSpecs.IdA3,
        });

        var result = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            return (Template: vm.SelectedTemplate?.Id, Sheet: vm.Sheet.SelectedSheetOption?.Spec.Id);
        });

        Assert.Equal(BuiltInTemplates.IdRowsFour, result.Template);
        Assert.Equal(BuiltInSheetSpecs.IdA3, result.Sheet);
    }

    /// <summary>
    /// 「一开四」当默认是上一版把裁切指令当成拼版的错误理解（五家真样张全部一页一枚）。
    /// 它被记下来只是因为我把它设成了默认，不等于用户主动选过，所以启动时要让位给一页一枚。
    /// </summary>
    [Fact]
    public void 记着一开四时让位给一页一枚()
    {
        var store = new UiStateStore(_dir);
        store.Save(new UiState
        {
            TemplateId = BuiltInTemplates.IdRowsFour,
            SheetSpecId = BuiltInSheetSpecs.IdCut4_280x200,
        });

        var sheet = OnSta(() => new MainViewModel(store).Sheet.SelectedSheetOption?.Spec.Id);

        Assert.Equal(BuiltInSheetSpecs.IdOnePerLabel, sheet);
    }

    [Fact]
    public void 记的那套被删了退回标准内置而不是空着()
    {
        var store = new UiStateStore(_dir);
        store.Save(new UiState { TemplateId = "user.gone", SheetSpecId = "user.gone" });

        var result = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            return (Template: vm.SelectedTemplate?.Id, Sheet: vm.Sheet.SelectedSheetOption?.Spec.Id);
        });

        // 兜底跟 ReloadTemplates 用同一个档：行式四行（上一版构造兜 IdStandard、刷新兜 IdRowsFour，
        // 冷启动与触发一次刷新后看到的不是同一套；而九字段的标准箱唛厂牌表根本填不满）
        Assert.Equal(BuiltInTemplates.IdRowsFour, result.Template);
        // 记的那套没了就退回当前默认档（一页一枚），而不是退成 null 让界面空着
        Assert.Equal(BuiltInSheetSpecs.IdOnePerLabel, result.Sheet);
    }

    [Fact]
    public void 用户换模板会写回状态文件()
    {
        var store = new UiStateStore(_dir);

        var written = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            var target = vm.TemplateOptions.First(t => t.Id == BuiltInTemplates.IdRowsBigTwo);
            vm.SelectedTemplate = target;
            return store.Load().TemplateId;
        });

        Assert.Equal(BuiltInTemplates.IdRowsBigTwo, written);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论
        }
    }
}
