using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using LabelGou.Core.Impos;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 68 棒：纸规跟着模板走（用户 2026-09-14 原话）。
/// <para>他要的是「只要长宽是 140×100，纸规自动变成 280×200 的 2×2 排布」，而且
/// <strong>不用跳转到④页面</strong>、<strong>没有预设档就不建纸规</strong>、
/// <strong>模板和纸规是绑定的</strong>（再用那个模板，纸规变回这张模板对应的那张）。
/// 判据三档，前一段命中就不再往下看：当前这张本来就装得下 → 他手动为这份模板挑过的那张 → 按单枚尺寸的预设档。</para>
/// <para>界面状态一律落临时目录，不写用户的 <c>%APPDATA%</c>（§五-48）。</para>
/// </summary>
public sealed class TemplateSheetBindingTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-sheetbind");

    /// <summary>MainViewModel 会碰 WPF 类型，必须在 STA 线程上造。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private UiStateStore Store() => new(_dir);

    private static TemplateOption Pick(MainViewModel vm, string templateId)
        => vm.TemplateOptions.First(t => t.Id == templateId);

    private static SheetOption Paper(MainViewModel vm, string specId)
        => vm.Sheet.SheetOptions.First(o => o.Spec.Id == specId);

    [Fact]
    public void 选一百四十乘一百的模板_纸规自动跟到一开四()
    {
        var store = Store();

        var result = OnSta(() =>
        {
            // 起点故意用「一页一枚」：他说的正是这张 140×100 的模板要用 280×200 那张开料。
            store.Save(new UiState { TemplateId = BuiltInTemplates.IdStandard, SheetSpecId = BuiltInSheetSpecs.IdOnePerLabel });
            var vm = new MainViewModel(store);
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdRowsFour);
            var working = vm.Sheet.Working;
            Assert.NotNull(working);
            return (Sheet: vm.Sheet.SelectedSheetOption?.Spec.Id,
                Die: (working!.LabelWidthMm, working.LabelHeightMm),
                Note: vm.Sheet.SheetFollowNote);
        });

        Assert.Equal(BuiltInSheetSpecs.IdCut4_280x200, result.Sheet);
        // 判据红检：换错成另一张同样 140×100 的档也算错，所以连刀模尺寸一起钉；
        // 说明句里的数字单独查一遍——它写错成"一枚 14×10"就是把他绕晕的那句话。
        Assert.Equal((140, 100), result.Die);
        Assert.Contains("一开四", result.Note);
        Assert.Contains("28×20", result.Note);  // 说的是整张纸 28×20，不是一枚 14×10
    }

    [Fact]
    public void 自动换纸不跳步骤也不替他新建纸规()
    {
        var store = Store();
        store.Save(new UiState { TemplateId = BuiltInTemplates.IdStandard, SheetSpecId = BuiltInSheetSpecs.IdOnePerLabel });

        var result = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            var (step, papers) = (vm.StepIndex, vm.Sheet.SheetOptions.Count);
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdRowsFour);
            return (Step: step, Papers: papers, AfterStep: vm.StepIndex, AfterPapers: vm.Sheet.SheetOptions.Count);
        });

        Assert.Equal(result.Step, result.AfterStep);      // 「不用跳转到④页面」
        Assert.Equal(result.Papers, result.AfterPapers);  // 只挑现成的档，没凭空多出一张纸
    }

    /// <summary>
    /// 他自己在 ④ 步点过的那张纸，就是这份模板往后的纸规——换开一趟再回来也还在。
    /// <para>这条同时挡住两个方向的错：自动换纸盖掉他的手工选择（他一直骂的「暗改」），
    /// 以及绑定记岔了导致回来还是系统那一档。</para>
    /// </summary>
    [Fact]
    public void 手动换过的纸_下次用同一份模板还会回来()
    {
        var store = Store();

        var result = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdRowsEight);      // 100×70
            var eight = vm.Sheet.SelectedSheetOption?.Spec.Id;
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdRowsFour);       // 140×100
            var four = vm.Sheet.SelectedSheetOption?.Spec.Id;

            // 他嫌一开四费纸，自己在 ④ 步点了「一页一枚」
            vm.Sheet.SelectedSheetOption = Paper(vm, BuiltInSheetSpecs.IdOnePerLabel);
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdRowsEight);      // 走开一趟（另一份模板）
            var away = vm.Sheet.SelectedSheetOption?.Spec.Id;
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdRowsFour);
            return (Eight: eight, Four: four, Away: away, Back: vm.Sheet.SelectedSheetOption?.Spec.Id,
                Bound: store.Load().TemplateSheetIds);
        });

        Assert.Equal(BuiltInSheetSpecs.IdCut8_280x200, result.Eight);
        Assert.Equal(BuiltInSheetSpecs.IdCut4_280x200, result.Four);
        Assert.Equal(BuiltInSheetSpecs.IdCut8_280x200, result.Away);
        Assert.Equal(BuiltInSheetSpecs.IdOnePerLabel, result.Back);             // 他的那张没被「140×100 就该一开四」顶掉
        Assert.Equal(BuiltInSheetSpecs.IdOnePerLabel, result.Bound?[BuiltInTemplates.IdRowsFour]);
    }

    /// <summary>没有预设档的尺寸（100×80）就保持现状：他说「就先不建纸规，做好后在 ④ 阶段调」。</summary>
    [Fact]
    public void 没有预设纸规的尺寸_纸保持不动也不建新的()
    {
        var store = Store();

        var result = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdRowsFour);       // 先拿到一开四
            var papers = vm.Sheet.SheetOptions.Count;
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdStandard);       // 100×80：表里没有这档刀模
            return (Sheet: vm.Sheet.SelectedSheetOption?.Spec.Id, Papers: papers,
                AfterPapers: vm.Sheet.SheetOptions.Count, Note: vm.Sheet.SheetFollowNote,
                Bound: store.Load().TemplateSheetIds?.ContainsKey(BuiltInTemplates.IdStandard) == true);
        });

        Assert.Equal(BuiltInSheetSpecs.IdCut4_280x200, result.Sheet);
        Assert.Equal(result.Papers, result.AfterPapers);                        // 一张纸也没多出来
        Assert.Empty(result.Note);                                              // 没动就不啰嗦
        Assert.False(result.Bound);                                             // 没给他凭空记一条绑定
    }

    /// <summary>
    /// ④ 步那句错配提示旁的「换成配套模板」= 拿模板去就他手上这张纸，所以那一次不能连带换纸。
    /// <para>构造里故意让那份配套模板另有绑定（A3）：没有抑制的话它会按绑定把纸搬去 A3，
    /// 错配当场重新出现 —— 等于他刚点下的修复被软件自己撤销了（有区分度：删掉抑制这条就红）。</para>
    /// </summary>
    [Fact]
    public void 换成配套模板那一次_不把刚对上的纸搬走()
    {
        var store = Store();

        var result = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdRowsFour);
            vm.Sheet.SelectedSheetOption = Paper(vm, BuiltInSheetSpecs.IdA3);      // 他给 140×100 那份记的是 A3
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdRowsEight);          // 换 100×70，纸跟着去一开八
            vm.Sheet.SelectedSheetOption = Paper(vm, BuiltInSheetSpecs.IdCut4_280x200); // 又手工点回一开四 → 与模板错配
            Assert.Contains("对不上", vm.TemplateSheetHint);
            vm.UseMatchingTemplateCommand.Execute(null);
            return (Sheet: vm.Sheet.SelectedSheetOption?.Spec.Id, Template: vm.SelectedTemplate?.Id,
                Note: vm.Sheet.SheetFollowNote, Hint: vm.TemplateSheetHint);
        });

        Assert.Equal(BuiltInTemplates.IdRowsFour, result.Template);                 // 按钮真换了模板
        Assert.Equal(BuiltInSheetSpecs.IdCut4_280x200, result.Sheet);               // 纸还在他刚点下的那一张
        Assert.Empty(result.Note);
        Assert.DoesNotContain("对不上", result.Hint);                              // 错配就此了结，没有复发
    }

    /// <summary>
    /// 自动跟过去的纸<strong>不算他的选择</strong>：全局那条「上次用的纸」不许被顶掉。
    /// <para>写顶了，下次他换个模板启动就会看到一张自己根本没点过的纸被当成基准。</para>
    /// </summary>
    [Fact]
    public void 自动换纸不写全局的上次纸规_手动换的才写()
    {
        var store = Store();
        store.Save(new UiState { TemplateId = BuiltInTemplates.IdStandard, SheetSpecId = BuiltInSheetSpecs.IdA3 });

        var result = OnSta(() =>
        {
            var vm = new MainViewModel(store);
            vm.SelectedTemplate = Pick(vm, BuiltInTemplates.IdRowsFour);       // 自动换成一开四
            var afterAuto = store.Load().SheetSpecId;
            vm.Sheet.SelectedSheetOption = Paper(vm, BuiltInSheetSpecs.IdA4);  // 他自己点 A4
            return (AfterAuto: afterAuto, AfterManual: store.Load().SheetSpecId, Current: vm.Sheet.SelectedSheetOption?.Spec.Id);
        });

        Assert.Equal(BuiltInSheetSpecs.IdA3, result.AfterAuto);                 // 自动那一下没写盘
        Assert.Equal(BuiltInSheetSpecs.IdA4, result.AfterManual);               // 他点的那一下写了
        Assert.Equal(BuiltInSheetSpecs.IdA4, result.Current);
    }

    /// <summary>
    /// 旧状态文件（没绑定那一格）读得进来，行为退回「按单枚尺寸找预设档」。
    /// <para>这条同时是新口径的说明：他上次留的 A3 是一张<strong>刀模跟随模板</strong>的通用纸，
    /// 不是为这份模板挑的那一档，所以启动时按 140×100 跟到一开四；他要 A3 就在 ④ 步点一下，
    /// 那点下去就绑给这份模板了（见 <see cref="手动换过的纸_下次用同一份模板还会回来"/>）。</para>
    /// </summary>
    [Fact]
    public void 旧状态文件缺绑定那一格_读得进来且按尺寸跟档()
    {
        File.WriteAllText(Path.Combine(_dir, "uistate.json"),
            "{\"TemplateId\":\"" + BuiltInTemplates.IdRowsFour + "\",\"SheetSpecId\":\"" + BuiltInSheetSpecs.IdA3 + "\"}",
            new UTF8Encoding(false));
        var store = Store();

        var result = OnSta(() =>
            (Bound: store.Load().TemplateSheetIds, Sheet: new MainViewModel(store).Sheet.SelectedSheetOption?.Spec.Id));

        Assert.Null(result.Bound);
        Assert.Equal(BuiltInSheetSpecs.IdCut4_280x200, result.Sheet);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }
}
