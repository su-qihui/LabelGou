using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LabelGou.App.ViewModels;

namespace LabelGou.App.Services;

/// <summary>
/// 主窗口里那两根裸 <strong>← / →</strong> 归谁（第 89 棒②，用户 2026-09-20：
/// 「在预览页面开启行检查时使用：向右箭头--下一张，向左箭头--上一张」）。
/// <para>规则单独拎出来放在这里，有两个原因：一是窗口那条 <c>if</c> 链已经五层深，判据进不去
/// （<c>MainWindow</c> 在测试进程里造不出来，见 <c>WizardStepTests</c> 的说明）；二是"让开谁"这件事
/// 只该有一个说法，散在窗口里写迟早和别的快捷键对不上。</para>
/// <para><strong>让开</strong>文本框、下拉、表格：那些地方箭头另有本职（移光标、换选项、走单元格），
/// 抢过去就是"打字翻不了页、翻页打不了字"。<strong>不让开</strong>步骤条那个 ListBox——
/// 用户点完步骤后焦点正落在那儿，让开它这条快捷键等于永远不生效；切步仍能用鼠标点。</para>
/// <para>接管之后一律算"这键我用了"（<c>e.Handled</c>），包括第一行再按 ← 那种翻不动的时候：
/// 翻不动却把键还给步骤条，表现就是他按一下左箭头整页跳回 ③ 步。</para>
/// </summary>
public static class PreviewArrows
{
    /// <summary>这一态下 ←/→ 是不是归"翻页"管。</summary>
    public static bool ClaimsRowPaging(MainViewModel vm, IInputElement? focused)
        => vm is { ArrowPagesRows: true } && FocusIsFree(focused);

    /// <summary>焦点落在这些控件上时不接管（其余控件一律让位给翻页）。</summary>
    public static bool FocusIsFree(IInputElement? focused)
        => focused is not (TextBox or PasswordBox or ComboBox or DataGrid);

    /// <summary>
    /// 按一次方向键：翻了（或这一态归我们、但已经到顶/到底翻不动）返回 true，
    /// 让调用方把 <see cref="KeyEventArgs.Handled"/> 置上；返回 false 才把键还给原来的控件。
    /// </summary>
    public static bool TryPage(MainViewModel vm, IInputElement? focused, Key key)
    {
        if (key is not (Key.Left or Key.Right)) return false;
        if (!ClaimsRowPaging(vm, focused)) return false;

        var command = key == Key.Left ? vm.PrevRecordCommand : vm.NextRecordCommand;
        if (command.CanExecute(null)) command.Execute(null);
        return true;
    }
}
