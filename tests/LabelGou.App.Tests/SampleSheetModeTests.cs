using System.Linq;
using LabelGou.App.ViewModels;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 无表格时的样张模式（第 104 棒，用户：「添加无表格时可以编辑，编辑完后可以打印，单张模式」）。
/// <para>两件事要钉住：<strong>①</strong> 没导表也能排出一版（走的是同一条拼版路，不另开第二条）；
/// <strong>②</strong> 这一版在界面上必须一路写着「样张」——打出去的纸长得像"这批货的纸"就是事故。</para>
/// </summary>
public class SampleSheetModeTests
{
    [Theory]
    [InlineData(0, true, 1)]     // 没数据但有模板：给一枚样张
    [InlineData(5, true, 5)]     // 有真标签：照真标签，样张那一路完全不参与
    [InlineData(0, false, 0)]    // 连模板都没有：仍 0——硬塞一枚就是凭空造版
    [InlineData(12, false, 12)]
    public void 排几枚这条账在有模板没模板时各怎么算(int labels, bool hasTemplate, int expected)
        => Assert.Equal(expected, ImpositionViewModel.LabelsIntoPlan(labels, hasTemplate));

    [Fact]
    public void 没导表时排得出那一版并且写明是样张()
    {
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
        Assert.False(vm.HasData);                       // 一条表都没导
        Assert.NotNull(vm.SelectedTemplate);            // 启动就选好了模板

        vm.Sheet.RebuildPlan();

        Assert.True(vm.Sheet.IsSampleSheet);
        Assert.NotNull(vm.Sheet.Plan);
        Assert.True(vm.Sheet.Plan!.PageCount >= 1);     // 有页可打，「打印整版」不再是空的一版
        Assert.StartsWith("【样张】", vm.Sheet.PlanText);
        Assert.Contains(vm.Sheet.SheetIssues, s => s.Contains("样张"));
    }

    [Fact]
    public void 样张那一枚画得出来并且带的是示意值()
    {
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
        vm.Sheet.RebuildPlan();

        var layout = vm.Sheet.LayoutFor(1);
        Assert.NotNull(layout);                                   // 屏幕与纸同一入口拿到的那一枚
        var text = string.Join("|", layout!.Items.OfType<LabelGou.Core.Layout.TextItem>().Select(t => t.Content));
        Assert.Contains("YOGA-PANT-SS", text);                    // 示意样例的货号（SampleRecords.StandardSample 那份，不是编的假数据）
    }

    [Fact]
    public void 导了表就不是样张那一行标记要自己消失()
    {
        var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
        vm.Sheet.RebuildPlan();
        Assert.True(vm.Sheet.IsSampleSheet);

        var csv = System.IO.Path.Combine(TestEnvironment.NewTempDir("sample-off"), "t.csv");
        System.IO.File.WriteAllText(csv, "货号 ITEM NO:,件数 CTN,数量 QTY\nolu830-35*144,5,144\n");
        Assert.True(vm.TryOpenFileAt(csv));
        vm.Sheet.RebuildPlan();

        Assert.False(vm.Sheet.IsSampleSheet);                     // 有真数据了，标记必须撤掉
        Assert.DoesNotContain("样张", vm.Sheet.PlanText);
    }
}
