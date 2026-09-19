using System.Text.Json;
using LabelGou.App.Services.Agent;
using LabelGou.Core.Agent;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 工具面的判据（第 86 棒）——这一页管的是"外面那个东西能对我们的数据做什么"。
/// <para>要钉的口径只有一句：<strong>读的一切都能读，写要老板勾过，出纸永远不给。</strong>
/// 出纸不在这里被"拦住"，是压根不注册（§七-10 闸一的结构性版：没有可点的东西，就没有能绕的闸）。</para>
/// </summary>
public class AgentToolCatalogTests
{
    static LabelGouToolCatalog Catalog(bool writesAllowed, bool applyWired = true)
        => new(
            () => new LabelGouToolCatalog.Snapshot("列 A 货号，列 B 箱数", 31, 1, "140×100 一开四", "280×200 一开四"),
            _ => new LabelGouToolCatalog.CheckOutcome(true, "读了：3 条被采纳，1 条要你拍板"),
            applyWired ? _ => (true, "已落地，可以一键撤回") : null,
            writesAllowed);

    static string[] Names(AgentHostKind host, bool writesAllowed = false, bool applyWired = true)
        => Catalog(writesAllowed, applyWired).Build(host).AvailableFor(host).Select(t => t.Name).ToArray();

    [Fact]
    public void 勾过允许写的界面宿主才有那一颗()
    {
        Assert.Contains("labelgou.apply_proposal", Names(AgentHostKind.WithUi, writesAllowed: true));
        Assert.DoesNotContain("labelgou.apply_proposal", Names(AgentHostKind.WithUi, writesAllowed: false));
    }

    [Fact]
    public void 不带界面的那一路连读表写口都拿不到()
    {
        // 头less 那一路没有真 ConfirmGate（它是 ?? true），所以任何 NeedsUiHost 的工具都不给它注册。
        var headless = Names(AgentHostKind.Headless, writesAllowed: true);
        Assert.DoesNotContain("labelgou.apply_proposal", headless);
        Assert.Contains("labelgou.describe_current_table", headless);
    }

    [Fact]
    public void 没接落地那一条口时写工具不注册()
    {
        // 老板勾了允许，但这台根本没把 ApplyAiProposal 接上（比如面板还没建）：那也不许出现一颗点了必炸的工具。
        Assert.DoesNotContain("labelgou.apply_proposal",
            Names(AgentHostKind.WithUi, writesAllowed: true, applyWired: false));
    }

    [Fact]
    public void 任何一档里都数不出会出纸的工具()
    {
        foreach (var host in new[] { AgentHostKind.WithUi, AgentHostKind.Headless })
        {
            var json = Catalog(writesAllowed: true).Build(host).ToolsListJson(host);
            Assert.DoesNotContain("print", json);
            Assert.DoesNotContain("export", json);
            Assert.DoesNotContain("save_template", json);
        }
    }

    [Fact]
    public void 没导表时如实说没有而不是编一份()
    {
        var empty = new LabelGouToolCatalog(() => null, _ => new LabelGouToolCatalog.CheckOutcome(true, "没人会读到这句"), null, false);
        var result = empty.Call("labelgou.describe_current_table", default);
        Assert.False(result.Ok);
        Assert.Contains("没", result.Text);
    }

    [Fact]
    public void 判一份提案这一颗不落任何东西()
    {
        var applied = 0;
        var catalog = new LabelGouToolCatalog(
            () => null,
            _ => new LabelGouToolCatalog.CheckOutcome(true, "两条要拍板"),
            _ => { applied++; return (true, "落了"); },
            writesAllowed: true);

        var args = JsonDocument.Parse("""{"proposalJson":"{}"}""").RootElement;
        Assert.Contains("要拍板", catalog.Call("labelgou.check_proposal", args).Text);
        Assert.Equal(0, applied);   // check 就是 check：调它不许把东西落到活表上
    }

    [Fact]
    public void 递进来的提案缺参数要说人话()
    {
        var result = Catalog(true).Call("labelgou.apply_proposal", default);
        Assert.False(result.Ok);
        Assert.Contains("proposalJson", result.Text);
    }
}
