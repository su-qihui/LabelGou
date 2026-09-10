using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using LabelGou.Core.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// AI 提案落到界面那一侧的钉子（第 33 棒按"直接落地 + 可撤回"重写，第 40 棒按**两阶段**再重写）。
/// <para><strong>第 40 棒的架构</strong>：用户 2026-09-10 给的方向是
/// 「用户发送表格 → AI 读取和理解 → 指出表格存在的问题（<strong>这层先不要对预览纸张进行调整</strong>）
/// → 收到用户反馈后再次理解 → 理解后进行自动排版」。他实测第 39 棒的评语是
/// 「效果仍然和以前一样乱改模版乱提问题」——根因就是第 33 棒那版解析一成功就整份落地，
/// 问题还摆在落地<em>之后</em>，先斩后奏。</para>
/// <para>这里钉五件事：① <strong>第一步绝不落地</strong>（哪怕模型不听话硬给了版式与纸规）；
/// ② 答复只改那份提案、并注入上下文；③ 答满才进第二步；④ 落地那份是"第一步的理解 + 第二步的版式"合成的；
/// ⑤ 落了地清单只读 + 能撤回。</para>
/// <para>不联网：走 <see cref="AiChatPanel.FeedProposalAnswer"/>（第一步）与
/// <see cref="AiChatPanel.FeedProposalLayoutAnswer"/>（第二步）喂真模型原文。</para>
/// </summary>
public sealed class AiProposalFlowTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-proposal");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清不掉不影响结论 */ }
    }

    /// <summary>
    /// 第一步的回包。<strong>刻意带着 rows 与 sheetSpec</strong>：提示词明说这一步不要给排版，
    /// 可模型会不听话——正好用它钉"解析层也拦一道，硬给了也落不了地"。
    /// </summary>
    private const string ProposalJson = """
        {
          "hasHeader": true,
          "headerRow": 3,
          "totalRows": [12, 13],
          "sheetSpec": "280×200 一开四",
          "facts": [ "第1列是货号", "第2列是每箱数量" ],
          "mappings": [ { "column": "货号 ITEM NO:", "field": "ItemNo" } ],
          "questions": [ { "text": "件数末尾总数155要不要印", "no": "不需要", "yes": "需要", "action": "row-keep", "row": 12 } ],
          "rows": [ { "content": "Ctns No.{{CartonNo}}/{{CartonTotal}}", "sizePt": 14, "weight": 1 },
                    { "content": "ITEM NO.{{ItemNo}}", "sizePt": 12, "weight": 1 } ]
        }
        """;

    /// <summary>第二步的回包：只有版式与纸（理解那些第一步定了，它不必再报）。</summary>
    private const string LayoutJson = """
        {
          "sheetSpec": "一页一枚（纸面跟标签走）",
          "rows": [ { "content": "Ctns No.{{CartonNo}}/{{CartonTotal}}", "sizePt": 14, "weight": 1 },
                    { "content": "ITEM NO.{{ItemNo}}", "sizePt": 12, "weight": 1 } ]
        }
        """;

    /// <summary>带两条问题的第一步回包（专门用来验"答满才进第二步"）。</summary>
    private const string TwoQuestionsJson = """
        {
          "facts": [ "第1列是货号" ],
          "questions": [
            { "text": "货号里*号和后面那截要不要印", "no": "不用", "yes": "要", "action": "itemno-tail" },
            { "text": "件数末尾总数155要不要印", "no": "不需要", "yes": "要", "action": "row-keep", "row": 12 }
          ]
        }
        """;

    private static AiChangeContext CurrentState() => new(
        RawRowCount: 13, HeaderRow: 1, HasHeader: true, ExcludedRows: null,
        TemplateName: "一开四 140×100", LabelWidthMm: 140, LabelHeightMm: 100,
        SheetSpecName: "A4 底纸");

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private static IEnumerable<Button> Buttons(DependencyObject node)
    {
        if (node is Button button) yield return button;
        if (node is Panel panel)
            for (var i = 0; i < panel.Children.Count; i++)
                foreach (var found in Buttons(panel.Children[i])) yield return found;
        if (node is ContentControl content && content.Content is DependencyObject inner)
            foreach (var found in Buttons(inner)) yield return found;
    }

    private static IEnumerable<string> Texts(DependencyObject node)
    {
        if (node is TextBlock block && !string.IsNullOrEmpty(block.Text)) yield return block.Text;
        if (node is Panel panel)
            for (var i = 0; i < panel.Children.Count; i++)
                foreach (var found in Texts(panel.Children[i])) yield return found;
        if (node is ContentControl content && content.Content is DependencyObject inner)
            foreach (var found in Texts(inner)) yield return found;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [Fact]
    public void 第一步绝不落地_哪怕模型硬给了版式与纸规()
    {
        var applied = new List<AiSheetProposal>();

        var text = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyProposal = proposal => { applied.Add(proposal); return (true, "切表✓ 字段绑定✓ 版式✓"); };
            panel.FeedProposalAnswer(ProposalJson);
            return panel.Transcript;
        });

        // 用户 2026-09-10 的红线：「指出表格存在的问题……这层先不要对预览纸张进行调整」。
        // 第 33 棒那版这里会被调一次（解析成功即整份落地），那就是他实测的「乱改模板」。
        Assert.Empty(applied);
        Assert.Contains("模板、纸张与预览一个字没动", text);
        // 模型不听话硬给了 rows 与 sheetSpec：解析层拦下来并如实说一句，不静默丢掉。
        Assert.Contains("没理它", text);
        // 这一步该摆出来的是"它把这张表读成了什么"与"要你拍板的事"。
        Assert.Contains("它把这张表读成了这样", text);
        Assert.Contains("列名在原表第 3 行", text);
    }

    [Fact]
    public void 答完问题才落地_落地那份是理解与版式合成的()
    {
        var applied = new List<AiSheetProposal>();

        OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyProposal = proposal => { applied.Add(proposal); return (true, "切表✓ 字段绑定✓ 版式✓"); };
            panel.FeedProposalAnswer(ProposalJson);
            Assert.Empty(applied);                                 // 第一步不落地

            Click(Buttons(panel).First(b => (b.Content as string) == "✅ 需要"));
            Assert.Empty(applied);                                 // 答题本身也不落地（离线跑不动第二步，只留一句话）
            panel.FeedProposalLayoutAnswer(LayoutJson);            // 第二步的回包到了才落
            return 0;
        });

        var one = Assert.Single(applied);
        // 纸与版式那半边取第二步的
        Assert.Equal("一页一枚（纸面跟标签走）", one.SheetSpecName);
        Assert.NotNull(one.Layout);
        // 理解那半边取第一步的（老板拍过板）：列名行还在，剔行名单按他的答复改过——
        // 他点「需要」= 第 12 行要印，所以 12 从名单里拿掉，只剩 13。
        Assert.Equal(3, one.HeaderRow);
        Assert.Equal(new[] { 13 }, one.TotalValueRows);
    }

    [Fact]
    public void 清单只读_有详情但没有逐条采用与取消()
    {
        var counts = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyProposal = _ => (true, "已落");
            panel.FeedProposalAnswer(ProposalJson);
            Click(Buttons(panel).First(b => (b.Content as string) == "✅ 需要"));
            panel.FeedProposalLayoutAnswer(LayoutJson);
            return (details: Buttons(panel).Count(b => (b.Content as string) == "详情"),
                    yes: Buttons(panel).Count(b => (b.Content as string) == "✅ 采用"),
                    no: Buttons(panel).Count(b => (b.Content as string) == "❌ 不执行"));
        });

        Assert.True(counts.details > 0, "清单还得看得见（落了地也要知道改了哪几处）");
        Assert.Equal(0, counts.yes);    // 逐条采用已经停用（第 33 棒）
        Assert.Equal(0, counts.no);
    }

    [Fact]
    public void 能撤时给出撤回按钮_点了真调到撤回入口()
    {
        var undoCalls = 0;

        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyProposal = _ => (true, "已落");
            panel.CanUndoAiChange = () => true;
            panel.AiUndoLabel = () => "读表提案（整份）";
            panel.UndoAiChange = () =>
            {
                undoCalls++;
                return (true, "已撤回「读表提案（整份）」这一步：字段绑定退回了 1 项。");
            };
            // 撤回按钮只在**落了地**之后才出现，所以要走完两步（第一步不落地）。
            panel.FeedProposalAnswer(ProposalJson);
            Click(Buttons(panel).First(b => (b.Content as string) == "✅ 需要"));
            panel.FeedProposalLayoutAnswer(LayoutJson);

            var undo = Buttons(panel).FirstOrDefault(b => (b.Content as string)?.StartsWith("↩", StringComparison.Ordinal) == true);
            Assert.NotNull(undo);
            Click(undo!);
            return Texts(panel).ToList();
        });

        Assert.Equal(1, undoCalls);
        Assert.Contains(probe, t => t.Contains("已撤回"));
    }

    [Fact]
    public void 问题仍然逐条问他_答案注入上下文_而这一步不落地()
    {
        var applied = 0;

        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyProposal = _ => { applied++; return (true, "已落"); };
            panel.FeedProposalAnswer(ProposalJson);

            // 问题那一块照旧：⚠ + 两颗按钮（那是唯一要他拍板的交互）。
            var yes = Buttons(panel).FirstOrDefault(b => (b.Content as string) == "✅ 需要");
            Assert.NotNull(yes);
            Click(yes!);
            return (turns: panel.Turns.Select(t => t.Text).ToList(),
                    decisions: panel.Decisions.ToList(),
                    text: panel.Transcript);
        });

        // 第 40 棒的核心：答复**只改那份提案**，不碰活表（用户红线「这层先不要对预览纸张进行调整」）。
        Assert.Equal(0, applied);
        // 用户的原话是「选择后将回答注入思考」：他的决定必须让 AI 下一步知道，否则下一步还是老判断。
        Assert.Contains(probe.turns, t => t.Contains("我对你这一问的决定", StringComparison.Ordinal));
        Assert.Contains("件数末尾总数155要不要印 → 需要", string.Join(" | ", probe.decisions));
        // 软件确定性办掉的那一件要如实说出来（不是含糊的一句"已办"）。
        Assert.Contains("照你说的，第 12 行当货印", probe.text);
    }

    [Fact]
    public void 第一步什么都没看出来_也照样给出下一步按钮()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            // 只有一句 reason 的回包 = 什么都没提（IsEmpty）。
            panel.FeedProposalAnswer("""{"reason":"这张表我没看出要改的"}""");
            return (again: Buttons(panel).Count(b => (b.Content as string)?.Contains("重读这张表") == true),
                    relayout: Buttons(panel).Count(b => (b.Content as string)?.Contains("只重排这一步") == true),
                    layout: Buttons(panel).Count(b => (b.Content as string) == "只让它排一版版式"),
                    text: panel.Transcript);
        });

        // 用户 2026-09-10：「即使是它觉得没问题，那不应该出现下一步的按键让 AI 来排版和绑定列吗」
        // ——以前这里只有一句技术话，人被晾在死路上。第 40 棒是三颗按钮各管一步。
        Assert.Equal(1, probe.again);
        Assert.Equal(1, probe.relayout);
        Assert.Equal(1, probe.layout);
        Assert.Contains("什么都没看出来", probe.text);
    }

    [Fact]
    public void 答一半不跑_答满才进第二步_而且不重摇第一步的理解()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyProposal = _ => (true, "已落");
            // 这一版带 2 条问题（ProposalJson 只有 1 条，所以这里用专用夹具）。
            panel.FeedProposalAnswer(TwoQuestionsJson);
            var yes = Buttons(panel).Where(b => (b.Content as string) == "✅ 要").ToList();
            Assert.Equal(2, yes.Count);
            Click(yes[0]);
            var afterOne = panel.Transcript;             // 只答一条：不该进第二步
            Click(yes[1]);
            return (afterOne, afterBoth: panel.Transcript, decisions: panel.Decisions.ToList());
        });

        // 他的决定进了清单（下一步的请求会带上它 —— 这条链以前是断的）
        Assert.Equal(2, probe.decisions.Count);
        Assert.Contains("→ 要", string.Join(" | ", probe.decisions));

        // 答一半不跑（每版一两分钟、也算一份钱），答满才进第二步
        Assert.Contains("还剩 1 条要你拍板", probe.afterOne);
        Assert.DoesNotContain("进第二步", probe.afterOne);
        Assert.Contains("进第二步", probe.afterBoth);
        // 关键：进的是**只排版的第二步**，不是把整份提案重摇一遍。以前这里是重发整份请求，
        // 十来个耦合输出一起重摇，于是第 1 版有 JP、第 2 版丢了、第 3 版空白（用户 2026-09-10 实拍）。
        Assert.DoesNotContain("重出第", probe.afterBoth);
    }
}
