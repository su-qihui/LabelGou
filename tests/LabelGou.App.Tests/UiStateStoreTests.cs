using System;
using System.IO;
using LabelGou.App.Services;
using LabelGou.Core.Impos;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 界面状态（记住上次用的模板与纸规）。
/// <para>
/// 目录一律注入临时路径：这个存储真实位置在 <c>%APPDATA%\LabelGou\</c>，测试要是用它，
/// 现场排障时就会看到用户机器上凭空多出一份状态文件（§五-48 那个坑的同类）。
/// </para>
/// </summary>
public sealed class UiStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "labelgou-uistate-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public void 没有状态文件时退回空而不是抛()
    {
        var state = new UiStateStore(_dir).Load();

        Assert.Null(state.TemplateId);
        Assert.Null(state.SheetSpecId);
    }

    [Fact]
    public void 存过的模板与纸规能被下一次启动读回()
    {
        new UiStateStore(_dir).Save(new UiState
        {
            TemplateId = BuiltInTemplates.IdRowsFour,
            SheetSpecId = BuiltInSheetSpecs.IdCut4_280x200,
        });

        var back = new UiStateStore(_dir).Load();

        Assert.Equal(BuiltInTemplates.IdRowsFour, back.TemplateId);
        Assert.Equal(BuiltInSheetSpecs.IdCut4_280x200, back.SheetSpecId);
    }

    [Fact]
    public void 只更新一个字段不会把另一个抹掉()
    {
        // 模板与纸规由两个 VM 分别写，读-改-写必须保住对方那份
        var store = new UiStateStore(_dir);
        store.Save(new UiState { TemplateId = BuiltInTemplates.IdRowsBigTwo });
        var state = store.Load();
        state.SheetSpecId = BuiltInSheetSpecs.IdCut4_280x200;
        store.Save(state);

        var back = store.Load();
        Assert.Equal(BuiltInTemplates.IdRowsBigTwo, back.TemplateId);
        Assert.Equal(BuiltInSheetSpecs.IdCut4_280x200, back.SheetSpecId);
    }

    [Fact]
    public void 状态文件坏了只退回默认不拦启动()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "uistate.json"), "{ 这不是 JSON");

        var state = new UiStateStore(_dir).Load();

        Assert.Null(state.TemplateId);
    }

    [Fact]
    public void 写不进去也不抛异常()
    {
        // 把"目录"做成一个文件：它的子路径建不出来 → Save 必须自己吞掉，不能弹给用户
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");

        new UiStateStore(blocker).Save(new UiState { TemplateId = "t" });
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
