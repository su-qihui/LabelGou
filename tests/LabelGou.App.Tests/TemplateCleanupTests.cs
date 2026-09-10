using System;
using System.IO;
using System.Linq;
using LabelGou.App.Services;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 「批量删模板」那一层的钉子（第 37 棒）。
/// <para><strong>为什么这几条必须钉</strong>：删除是**不可逆**的动作，而要删的名单来自界面勾选——
/// 写死在窗口按钮里就只能靠"点一遍看看"来验。抽出来之后就能拿临时目录真删一次，把它钉死：
/// 备份到底建没建、内置到底动没动、没勾到底删不删。</para>
/// <para>起因是用户 2026-09-10 那句「那如果有用的模版我也删了吗??」——他要删那 14 个同名的
/// 「AI 建议版式」，但怕连有用的那份一起删掉。所以"先备份再删"是这一屏的核心承诺，不是附赠功能。</para>
/// </summary>
public sealed class TemplateCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "labelgou-tmclean-" + Guid.NewGuid().ToString("N")[..8]);

    public TemplateCleanupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清不掉不影响结论 */ }
    }

    /// <summary>存一份真模板（Save 会过校验，所以得给一个像样的元素）。</summary>
    private static string SaveOne(TemplateStore store, string name)
    {
        var template = new LabelTemplate { Name = name, WidthMm = 140, HeightMm = 100 };
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text,
            Text = "ITEM NO：{{ItemNo}}",
            X = 5, Y = 5, Width = 120, Height = 12,
        });
        var (saved, _, issues) = store.Save(template);
        Assert.True(saved, string.Join("；", issues.Select(i => i.Message)));
        return template.Id;
    }

    [Fact]
    public void 批量删除会先备份_内置那份连备份名单都进不去()
    {
        var store = new TemplateStore(_dir);
        var a = SaveOne(store, "AI 建议版式");
        var b = SaveOne(store, "客户 A 的唛头");
        var builtIn = store.ListAll().First(t => t.BuiltIn).Id;

        var (deleted, skipped, backupDir) = TemplateCleanup.DeleteWithBackup(store, new[] { a, b, builtIn });

        Assert.Equal(2, deleted);
        Assert.Equal(1, skipped);                                     // 内置的被跳过（不是删失败）
        Assert.NotNull(backupDir);
        Assert.Equal(2, Directory.GetFiles(backupDir!, "*.json").Length);   // 备份是真建了的
        Assert.Null(store.FindFileFor(a));                            // 原文件真删了
        Assert.Null(store.FindFileFor(b));
        Assert.NotNull(store.ListAll().FirstOrDefault(t => t.BuiltIn));      // 内置那份还在
    }

    [Fact]
    public void 一份都没勾的时候不建空目录也不碰文件()
    {
        var store = new TemplateStore(_dir);
        var a = SaveOne(store, "AI 建议版式");

        var (deleted, _, backupDir) = TemplateCleanup.DeleteWithBackup(store, Array.Empty<string>());

        Assert.Equal(0, deleted);
        Assert.Null(backupDir);                                       // 不留空目录（免得目录越攒越多）
        Assert.NotNull(store.FindFileFor(a));
    }

    [Fact]
    public void 备份目录是模板目录的子目录_所以不会重新出现在模板列表里()
    {
        var store = new TemplateStore(_dir);
        var a = SaveOne(store, "AI 建议版式");

        var (_, _, backupDir) = TemplateCleanup.DeleteWithBackup(store, new[] { a });

        Assert.NotNull(backupDir);
        Assert.StartsWith(store.UserDirectory, backupDir!, StringComparison.OrdinalIgnoreCase);
        // 模板库只枚举顶层 *.json —— 备份躺在子目录里，所以删完列表里不会又冒出来一份。
        Assert.DoesNotContain(store.ListAll(), t => !t.BuiltIn && t.Name == "AI 建议版式");
    }
}
