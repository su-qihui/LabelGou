using System.IO;
using System.Windows;
using System.Windows.Controls;
using LabelGou.App.Rendering;
using LabelGou.App.ViewModels;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 83 棒：①点选命中——<strong>谁的字看得见就选谁</strong>，上面那条通栏行带不许抢；
/// ②双击一行文字就地改内容，框里给的是<strong>模板原文（占位符）</strong>而不是屏幕上那份样例值。
/// <para>①的夹具照用户截图里那两行的真实数字做（02 的行带 130×123.37、从 y=−5.4 起盖住整张纸）；
/// ②判的是"开出来的框里到底写着什么"——写样例值就是把字段写死成数字的那条老路（第 73 棒红线）。</para>
/// </summary>
public class EditorHitAndInlineEditTests
{
    private static void OnStaThread(Action work)
        => LabelGou.App.Export.StaWorker
            .RunAsync<bool>((_, _) => { work(); return true; }, null, CancellationToken.None)
            .GetAwaiter().GetResult();

    private static TemplateEditorViewModel NewVm(LabelTemplate working)
    {
        var folder = Path.Combine(Path.GetTempPath(), "labelgou-b83-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        return new TemplateEditorViewModel(working, new TemplateStore(folder));
    }

    private static TemplateElement Text(string content, double x, double y, double w, double h, double pt = 12) => new()
    {
        Kind = ElementKind.Text, Text = content, X = x, Y = y, Width = w, Height = h,
        FontSizePt = pt, WrapWidthMm = w,
    };

    /// <summary>用户截图那份形状：01 的字在左上，02 的通栏行带压在整张纸上（它在图层里更靠上）。</summary>
    private static LabelTemplate TwoLines() => new()
    {
        Id = "user.b83", Name = "两行叠着", WidthMm = 140, HeightMm = 100, PaddingMm = 5, BorderMm = 0,
        Elements =
        {
            Text("第一行文字要长一点才盖得住", 5, 5, 130, 20),
            Text("第二行文字", 19.5, -5.4, 130, 123.37),
        },
    };

    [Fact]
    public void ClickingTheLowerLinesTextPicksThatLineNotTheUpperBand() => OnStaThread(() =>
    {
        var vm = NewVm(TwoLines());
        var lower = vm.Template.Elements[0];
        var upper = vm.Template.Elements[1];
        var ink = vm.DisplayBoxOf(lower)!.Value;

        // 点在 01 看得见的那一块正中（同时也确实在 02 那条 130×123 的行带里）
        var picked = vm.TextAtForEdit(ink.X + ink.Width / 2, ink.Y + ink.Height / 2);

        Assert.Same(lower, picked);
        Assert.NotSame(upper, picked);
        // 夹具自检：这一点必须在 02 的行带里，否则这条测不到"抢"
        Assert.True(upper.X <= ink.X + ink.Width / 2 && ink.X + ink.Width / 2 <= upper.X + upper.Width
                    && upper.Y <= ink.Y + ink.Height / 2 && ink.Y + ink.Height / 2 <= upper.Y + upper.Height,
            "夹具没做出【上层行带盖住下层字】的形状，测不到东西");
    });

    [Fact]
    public void ClickingEmptySpaceStillSelectsTheUpperBandSoLinesStayPickable() => OnStaThread(() =>
    {
        var vm = NewVm(TwoLines());
        var upper = vm.Template.Elements[1];

        // 纸面上谁都碰不到字的空白：第二遍宽容盒照旧兜住（第 44 棒"点旁边空白也选得中这一行"不许丢）
        var picked = vm.TextAtForEdit(120, 50);

        Assert.Same(upper, picked);
    });

    [Fact]
    public void TheInlineEditorShowsThePlaceholdersNotTheSampleValue() => OnStaThread(() =>
    {
        var template = new LabelTemplate
        {
            Id = "user.b83d", Name = "带字段的一行", WidthMm = 140, HeightMm = 100, PaddingMm = 5, BorderMm = 0,
            Elements = { Text("QTY:{{col:QTY}}PCS", 5, 20, 130, 20, pt: 30) },
        };
        var vm = NewVm(template);
        var text = template.Elements[0];

        var draft = vm.InlineEditDraftFor(text);

        Assert.Equal("QTY:{{col:QTY}}PCS", draft);
        // 画布上那份是样例值：两者必须不同，否则这条测不到"编辑框里给的是哪一个"
        var onScreen = vm.SampleLayout.Items.OfType<Core.Layout.TextItem>()
            .Single(i => ReferenceEquals(i.Source, text)).Content;
        Assert.NotEqual(draft, onScreen);
    });

    [Fact]
    public void CommittingAnInlineEditChangesTheTextAndUndoesAsOneStep() => OnStaThread(() =>
    {
        var vm = NewVm(TwoLines());
        var text = vm.Template.Elements[0];

        Assert.True(vm.CommitInlineEdit(text, "第一行：{{col:货号}}"));
        Assert.Equal("第一行：{{col:货号}}", text.Text);
        Assert.True(vm.IsDirty);

        vm.Undo();
        // 撤销是按快照把元素换回去的（第 62 棒③记过），攥着旧引用只会看到改后的那份 → 回读列表
        Assert.Equal("第一行文字要长一点才盖得住", vm.Template.Elements[0].Text);
    });

    [Fact]
    public void CancellingAnInlineEditLeavesTheTemplateAlone() => OnStaThread(() =>
    {
        var vm = NewVm(TwoLines());
        var window = new TemplateEditorWindow(vm);
        var ink = vm.DisplayBoxOf(vm.Template.Elements[0])!.Value;

        Assert.True(window.EditorCanvas.RequestInlineEditAt(ink.X + ink.Width / 2, ink.Y + ink.Height / 2));
        var editor = Assert.Single(window.InlineEditLayerForTests.Children.OfType<TextBox>());
        Assert.Equal("第一行文字要长一点才盖得住", editor.Text);            // 开出来的就是模板原文

        editor.Text = "改成别的";
        window.CloseInlineEditor(commit: false);                            // Esc 那一条路

        Assert.Equal("第一行文字要长一点才盖得住", vm.Template.Elements[0].Text);
        Assert.False(vm.IsDirty);
        Assert.Empty(window.InlineEditLayerForTests.Children);
    });

    [Fact]
    public void DoubleClickOnEmptyCanvasDoesNotOpenAnEditor() => OnStaThread(() =>
    {
        var vm = NewVm(TwoLines());
        var window = new TemplateEditorWindow(vm);

        Assert.False(window.EditorCanvas.RequestInlineEditAt(5, 90));   // 两条行带都碰不到的空白
        Assert.Empty(window.InlineEditLayerForTests.Children);
    });
}
