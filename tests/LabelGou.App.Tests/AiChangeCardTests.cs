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
using LabelGou.Core.Data;
using LabelGou.Core.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 「AI 改动确认卡」在界面那一侧的钉子（阶段 29 第 1 棒）。
/// <para>用户 2026-09-10 的三条决议里，这一条是红线：<strong>AI 的每一次写都要弹一张
/// 「改哪里 + 原值 → 新值」的 ✅/❌ 卡，人点 ✅ 才落地</strong>。他还纠正过一版写法——
/// 「人手动改后也可能出错，所以不该靠『人的改动一定对』来防冲突」。</para>
/// <para>这里钉四件事：① 卡片真带原值与新值（不是只说"会改成什么"）；
/// ② <strong>点 ✅ 才落地、点 ❌ 什么都不碰</strong>；③ <strong>卡过期一律作废</strong>
/// （等它出方案时人改了别处，卡上的"原值"就不是现在了，拿旧原值盖新状态 = 静默改错东西）；
/// ④ 逐条落地只动被点名的那一类，其余按当前切法原样保留。</para>
/// <para>不联网：走 <see cref="AiChatPanel.FeedProposalAnswer"/> 把真模型原文喂进去跑完整条路
/// （与 <see cref="AiAssistantPanelTests"/> 同一套路）；WPF 断言一律留 STA 线程内（§五-91）。</para>
/// </summary>
public sealed class AiChangeCardTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-aichange");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清不掉不影响结论 */ }
    }

    /// <summary>四类改动各一条都出得来（表头行 / 剔除行 / 版式 / 纸）。</summary>
    private const string ProposalJson = """
        {
          "hasHeader": true,
          "headerRow": 3,
          "totalRows": [12, 13],
          "sheetSpec": "280×200 一开四",
          "reason": "第 3 行别名命中最多，末尾两行是合计",
          "rows": [ { "content": "Ctns No.{{CartonNo}}/{{CartonTotal}}", "sizePt": 14, "weight": 1 },
                    { "content": "ITEM NO.{{ItemNo}}", "sizePt": 12, "weight": 1 } ]
        }
        """;

    /// <summary>软件此刻的样子：列名在第 1 行、没剔行、纸是 A4 底纸。</summary>
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

    /// <summary>断言"有哪一行文字含这段话"。不用 <c>Assert.Contains(string, IEnumerable&lt;string&gt;)</c>：
    /// 那个是全等匹配，而卡片上的文案前面带 <c>· </c> 前缀、差值行是拼出来的。</summary>
    private static void AssertAnyText(IEnumerable<string> texts, string fragment)
    {
        var list = texts.ToList();
        Assert.True(list.Any(t => t.Contains(fragment, StringComparison.Ordinal)),
            $"没有哪一行文字含有「{fragment}」。实际：{string.Join(" | ", list)}");
    }

    [Fact]
    public void 改动卡逐条摆出原值与新值_不用点开别处就知道要改什么()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyChange = (_, _, _) => (true, "ok");
            panel.FeedProposalAnswer(ProposalJson);
            return (yes: Buttons(panel).Count(b => (b.Content as string) == "✅ 采用"),
                    no: Buttons(panel).Count(b => (b.Content as string) == "❌ 不执行"),
                    texts: Texts(panel).ToList());
        });

        Assert.Equal(4, probe.yes);                       // 四类各一张卡
        Assert.Equal(4, probe.no);
        AssertAnyText(probe.texts, "它打算改这 4 处");
        AssertAnyText(probe.texts, "列名在第几行");                      // 改哪里
        AssertAnyText(probe.texts, "列名在第 1 行 → 列名在第 3 行");       // 原值 → 新值（旧清单只有后半截）
        AssertAnyText(probe.texts, "没剔任何行（整张表都按货印） → 第 12、13 行不印");
        AssertAnyText(probe.texts, "现在这张「一开四 140×100」");          // 现在这张进了"原值"
    }

    [Fact]
    public void 卡片默认折叠_点详情才展开_按钮文案是不执行与采用()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.ApplyChange = (_, _, _) => (true, "ok");
            panel.FeedProposalAnswer(ProposalJson);

            var before = (details: Buttons(panel).Count(b => (b.Content as string) == "详情"),
                          collapses: Buttons(panel).Count(b => (b.Content as string) == "收起"),
                          no: Buttons(panel).Count(b => (b.Content as string) == "❌ 不执行"),
                          yes: Buttons(panel).Count(b => (b.Content as string) == "✅ 采用"));

            // 点开第一张卡的详情：它自己变成「收起」，其余三张仍是「详情」。
            Click(Buttons(panel).First(b => (b.Content as string) == "详情"));
            var after = (details: Buttons(panel).Count(b => (b.Content as string) == "详情"),
                         collapses: Buttons(panel).Count(b => (b.Content as string) == "收起"));
            return (before, after);
        });

        // 用户 2026-09-10：「这个采用先以折叠（点击展开）」——卡片摊着时四条就把面板下半截顶没了。
        Assert.Equal(4, probe.before.details);      // 四张卡各自一个「详情」
        // 基数说明：「收起」不是从 0 起——面板上方"思考过程"那颗按钮也叫「收起」（第 31 棒加的那块）。
        Assert.Equal(1, probe.before.collapses);    // 默认全收起，只有思考那一颗
        Assert.Equal(4, probe.before.no);           // 按钮文案照他写的：❌ 不执行 / ✅ 采用
        Assert.Equal(4, probe.before.yes);
        Assert.Equal(3, probe.after.details);       // 点开一张：它变成「收起」
        Assert.Equal(2, probe.after.collapses);     // 那一张的「收起」+ 思考的「收起」
    }

    [Fact]
    public void 点采用才交出去_而且一次只交那一条()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            var applied = new List<(AiChangeKind Kind, bool Yes)>();
            panel.ApplyChange = (_, change, yes) => { applied.Add((change.Kind, yes)); return (true, "已办"); };
            panel.FeedProposalAnswer(ProposalJson);

            // 第一张卡是「列名在第几行」（DescribeChanges 的固定次序）。
            Click(Buttons(panel).First(b => (b.Content as string) == "✅ 采用"));
            return (applied, text: panel.Transcript);
        });

        var one = Assert.Single(probe.applied);              // 只落这一条，不是整份
        Assert.Equal(AiChangeKind.HeaderRow, one.Kind);
        Assert.True(one.Yes);
        Assert.Contains("已办", probe.text);
    }

    [Fact]
    public void 点取消什么都不碰_并如实说这条没动()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            var applied = new List<bool>();
            panel.ApplyChange = (_, _, yes) => { applied.Add(yes); return (true, "「列名在第几行」照你说的不动。"); };
            panel.FeedProposalAnswer(ProposalJson);

            Click(Buttons(panel).First(b => (b.Content as string) == "❌ 不执行"));
            return (applied, text: panel.Transcript);
        });

        var only = Assert.Single(probe.applied);
        Assert.False(only);                                  // 交的是"不要"，落地那边负责什么都不碰
        Assert.Contains("不动", probe.text);
    }

    [Fact]
    public void 卡过期一律作废_不许拿旧原值往新状态上盖()
    {
        var probe = OnSta(() =>
        {
            var generation = 1;
            var panel = new AiChatPanel
            {
                GetChangeContext = CurrentState,
                GetDataGeneration = () => generation,
            };
            var applied = 0;
            panel.ApplyChange = (_, _, _) => { applied++; return (true, "ok"); };
            panel.FeedProposalAnswer(ProposalJson);           // 出卡时记下第 1 代

            generation = 2;                                   // 等它出方案这段时间，人改了别处
            Click(Buttons(panel).First(b => (b.Content as string) == "✅ 采用"));
            return (applied, text: panel.Transcript);
        });

        Assert.Equal(0, probe.applied);                       // 一个字节都没落地
        Assert.Contains("作废", probe.text);
    }

    [Fact]
    public void 没接落地入口时不出现改动卡_退回旧文字清单不静默丢信息()
    {
        var probe = OnSta(() =>
        {
            var panel = new AiChatPanel { GetChangeContext = CurrentState };
            panel.FeedProposalAnswer(ProposalJson);           // ApplyChange 故意不挂
            return (cards: Buttons(panel).Count(b => (b.Content as string) == "✅ 采用"),
                    text: panel.Transcript);
        });

        Assert.Equal(0, probe.cards);
        Assert.Contains("列名按你说的算", probe.text);          // 旧清单（DescribeItems）还在兜底
    }

    // ───────────────────────── 逐条落地：只动被点名的那一类 ─────────────────────────

    private string WriteThirteenRowCsv()
    {
        var text = new StringBuilder("流水号,货号 ITEM NO:,件数 CTN").AppendLine();
        for (var i = 1; i <= 12; i++) text.AppendLine($"AJ{i},olu830-{i},{i % 5 + 1}");
        var path = Path.Combine(_dir, "十三行.csv");
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(true));
        return path;
    }

    [Fact]
    public void 逐条落地只动被点名的那一类_列名行不会跟着动()
    {
        var csv = WriteThirteenRowCsv();

        var probe = OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            vm.LoadSource(csv, null);
            // 纸规名故意不传：这条只关心"表头行 / 剔除行"怎么合，别让纸规校验把提案整份判死。
            var proposal = AiSheetProposal.Parse(ProposalJson, null, vm.RawRowCount, sheetSpecNames: null);
            return (before: vm.CurrentChoice,
                    onlyRows: vm.ChoiceFrom(proposal, new[] { AiChangeKind.ExcludedRows }),
                    onlyHeader: vm.ChoiceFrom(proposal, new[] { AiChangeKind.HeaderRow }),
                    proposal.HeaderRow);
        });

        // 前置：此刻是「自动猜列名、一行没剔」。
        Assert.Null(probe.before.HeaderRowIndex);
        Assert.Equal(0, probe.before.ExcludedCount);
        Assert.Equal(3, probe.HeaderRow);                         // 提案原话：列名在原表第 3 行

        // 只 ✅ 了「哪几行不当货印」：列名行必须保持原样。
        Assert.Null(probe.onlyRows.HeaderRowIndex);
        Assert.Equal(new[] { 11, 12 }, probe.onlyRows.ExcludedRawRows);

        // 只 ✅ 了「列名在第几行」：剔除名单必须保持原样。
        Assert.Equal(2, probe.onlyHeader.HeaderRowIndex);          // 第 3 行 → 下标 2
        Assert.Null(probe.onlyHeader.ExcludedRawRows);
    }
}
