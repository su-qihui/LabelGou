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
/// AI 提案落到界面那一侧的钉子（第 33 棒按新架构重写）。
/// <para><strong>架构变了</strong>：用户 2026-09-10 把交互改成
/// 「输入表格 → AI 思考 → 只列出要他拍板的问题 → 选择后回答注入思考 → **直接注入预览** → 右侧可撤回」。
/// 于是第 29~32 棒那套"逐条 ✅/❌ 卡"**停用**：能自动判的直接落地，清单变成**只读**的"它改了这几处"，
/// 另给一颗**逐步撤回**。安全模型从"事前逐条点头"换成"事后看得见 + 退得回"。</para>
/// <para>这里钉三件事：① 读完**直接调落地入口**（不再等人点卡）；② 清单只读（有「详情」没有逐条采用）；
/// ③ 能撤时给出撤回按钮、点了真调到撤回入口。</para>
/// <para>不联网：走 <see cref="AiChatPanel.FeedProposalAnswer"/> 喂真模型原文（与 <see cref="AiAssistantPanelTests"/> 同路）。</para>
/// </summary>
public sealed class AiProposalFlowTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-proposal");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清不掉不影响结论 */ }
    }

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
    public void 读完自动落地_不再等人点卡()
    {
        var applied = new List<AiSheetProposal>();

        OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyProposal = proposal => { applied.Add(proposal); return (true, "切表✓ 字段绑定✓ 版式✓"); };
            panel.FeedProposalAnswer(ProposalJson);
            return 0;
        });

        // 第 33 棒：能自动判的直接进预览（用户的原话是"直接注入预览及其他"）——
        // 这条断言的就是"落地入口被主动调了一次"，而不是"在等谁点按钮"。
        var one = Assert.Single(applied);
        Assert.Equal("280×200 一开四", one.SheetSpecName);
    }

    [Fact]
    public void 清单只读_有详情但没有逐条采用与取消()
    {
        var counts = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyProposal = _ => (true, "已落");
            panel.FeedProposalAnswer(ProposalJson);
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
            panel.FeedProposalAnswer(ProposalJson);

            var undo = Buttons(panel).FirstOrDefault(b => (b.Content as string)?.StartsWith("↩", StringComparison.Ordinal) == true);
            Assert.NotNull(undo);
            Click(undo!);
            return Texts(panel).ToList();
        });

        Assert.Equal(1, undoCalls);
        Assert.Contains(probe, t => t.Contains("已撤回"));
    }

    [Fact]
    public void 问题仍然逐条问他_而且答案会注入上下文()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyProposal = _ => (true, "已落");
            panel.ApplyQuestion = (_, _, yes) => (true, yes ? "照你说的要印" : "照你说的不印");
            panel.FeedProposalAnswer(ProposalJson);

            // 问题那一块照旧：⚠ + 两颗按钮（那是唯一要他拍板的交互）。
            var yes = Buttons(panel).FirstOrDefault(b => (b.Content as string) == "✅ 需要");
            Assert.NotNull(yes);
            Click(yes!);
            return (turns: panel.Turns.ToList(), text: panel.Transcript);
        });

        Assert.Contains("照你说的要印", probe.text);
        // 用户的原话是「选择后将回答注入思考」：他的决定必须让 AI 下一轮知道，否则下一次读表还是老判断。
        Assert.Contains(probe.turns, t => t.Text.Contains("我对你这一问的决定", StringComparison.Ordinal));
    }
}
