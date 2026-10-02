using System;
using System.IO;
using LabelGou.Core.Recognition;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 「AI 提示语开放窗口」那套（第 103 棒）：清单本身的自洽、覆盖的读法、守门判据、以及
/// <strong>覆盖真的会进到发出去的那句话里</strong>（不然这扇窗只是个摆设）。
/// </summary>
public class PromptOverrideTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(),
        "labelgou-prompts-" + Guid.NewGuid().ToString("N")[..8]);

    // ===== 清单自洽：出厂稿必须能通过自己的守门判据 =====

    [Fact]
    public void 八段的键不重复并且都有出厂版文字()
    {
        var keys = PromptCatalog.All.Select(s => s.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(PromptCatalog.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.DefaultText));
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.False(string.IsNullOrWhiteSpace(s.Step));
        });
    }

    /// <summary>
    /// 这条是这一扇窗的地基：<strong>软件自带的那份稿子必须存得回去</strong>。
    /// 要是出厂稿自己就少了某个"地基词"，他一进来点保存就被拒，等于这扇窗天生坏了。
    /// </summary>
    [Fact]
    public void 出厂稿自己过得了守门判据()
        => Assert.All(PromptCatalog.All, s => Assert.Null(PromptCatalog.Validate(s, s.DefaultText)));

    [Fact]
    public void 白名单那七个动作名是地基词()
    {
        var ask = PromptCatalog.Find(PromptKeys.ReadAsk)!;
        Assert.Contains("itemno-tail", ask.RequiredTokens);
        Assert.Contains("column-meaning", ask.RequiredTokens);
        Assert.Equal(7, ask.RequiredTokens.Length);
    }

    // ===== 守门判据 =====

    [Fact]
    public void 空文与缺地基词都不许存()
    {
        var ask = PromptCatalog.Find(PromptKeys.ReadAsk)!;
        Assert.NotNull(PromptCatalog.Validate(ask, "   "));
        var missing = ask.RequiredTokens.Aggregate(ask.DefaultText, (text, token) => text.Replace(token, "某某"));
        var problem = PromptCatalog.Validate(ask, missing);
        Assert.NotNull(problem);
        Assert.Contains("itemno-tail", problem, StringComparison.Ordinal);
        // 少一个也不行，但报话里只点名缺的那几个（他才知道要补哪几个）
        Assert.DoesNotContain("第 40 棒", problem, StringComparison.Ordinal);
    }

    // ===== 覆盖的读法 =====

    [Fact]
    public void 存了就读得到_删了就回出厂()
    {
        var dir = TempDir();
        var store = new PromptOverrideStore(dir);
        var section = PromptCatalog.Find(PromptKeys.ReadTone)!;

        Assert.Null(store.Load(section.Key));
        Assert.False(store.IsOverridden(section.Key));
        Assert.Equal(section.DefaultText, new PromptTexts(store.Snapshot()).Get(section.Key));

        store.Save(section.Key, "一律用中文，一句话只说一件事。");
        Assert.True(store.IsOverridden(section.Key));
        Assert.Equal(new[] { section.Key }, store.OverriddenKeys());
        Assert.Equal("一律用中文，一句话只说一件事。", new PromptTexts(store.Snapshot()).Get(section.Key));

        store.Clear(section.Key);
        Assert.False(store.IsOverridden(section.Key));
        Assert.Equal(section.DefaultText, new PromptTexts(store.Snapshot()).Get(section.Key));
    }

    [Fact]
    public void 空文件与坏数据都当没改过而不是拦人干活()
    {
        var dir = TempDir();
        var section = PromptCatalog.Find(PromptKeys.LayoutTone)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, section.Key + ".txt"), "   ");   // 手贱存了个空文件
        Assert.Null(new PromptOverrideStore(dir).Load(section.Key));
        Assert.Equal(section.DefaultText, new PromptTexts(new PromptOverrideStore(dir).Snapshot()).Get(section.Key));
        // 目录压根不存在（第一次跑、或被删了）也不许抛
        Assert.Null(new PromptOverrideStore(Path.Combine(dir, "没有这一层")).Load(section.Key));
    }

    // ===== 端到端：覆盖真的进到发出去的那句话里 =====

    [Fact]
    public void 改过的那段会出现在发出去的提示词里()
    {
        var store = new PromptOverrideStore(TempDir());
        var read = PromptCatalog.Find(PromptKeys.ReadAsk)!;
        // 保留七个 action 名（不然过不了守门判据），只把开头那句换成他自己的说法
        var edited = "\n**只许问下面这几类**（我自己定的说法）：\n"
                     + string.Join("\n", read.RequiredTokens.Select(t => "  · " + t));
        Assert.Null(PromptCatalog.Validate(read, edited));
        store.Save(read.Key, edited);

        var prompt = AiSheetProposalPrompt.BuildRead("（画像）", 13, 1, 0, null, new PromptTexts(store.Snapshot()));

        Assert.Contains("我自己定的说法", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("问的规矩（第 34 棒定过", prompt, StringComparison.Ordinal);   // 出厂那版整段被换掉
        // 没改过的段落照旧在：一段被改不该把别段也带走
        Assert.Contains("最重要的一件事", prompt, StringComparison.Ordinal);
        Assert.Contains("字段清单", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void 第二步的覆盖只影响第二步()
    {
        var store = new PromptOverrideStore(TempDir());
        var font = PromptCatalog.Find(PromptKeys.LayoutFont)!;
        store.Save(font.Key, "字号你自己定，软件不管。" + string.Join("", font.RequiredTokens));

        var prompts = new PromptTexts(store.Snapshot());
        var layout = AiSheetProposalPrompt.BuildLayout("（画像）", new[] { "一开四" }, 13,
            AiSheetProposal.Parse("{\"facts\":[\"第1列是货号\"]}", null, 13, AiProposalStage.Read),
            "140×100 mm", 0, prompts);
        Assert.Contains("字号你自己定", layout, StringComparison.Ordinal);

        var read = AiSheetProposalPrompt.BuildRead("（画像）", 13, 1, 0, null, prompts);
        Assert.DoesNotContain("字号你自己定", read, StringComparison.Ordinal);
    }
}
