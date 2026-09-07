using System.Collections.Generic;
using System.Windows;

namespace LabelGou.App;

/// <summary>
/// 「整批固定值」对话框（M7）。行数据由 <see cref="ViewModels.MainViewModel.BuildFixedValueRows"/> 造好传进来，
/// 用户在文本框里改的就是同一批对象的 <c>Value</c>，所以确定时把同一个列表交回去即可，不做二次映射。
/// </summary>
public partial class FixedValuesWindow : Window
{
    private readonly IReadOnlyList<ViewModels.MainViewModel.FixedValueRow> _rows;

    public FixedValuesWindow(IReadOnlyList<ViewModels.MainViewModel.FixedValueRow> rows)
    {
        InitializeComponent();
        _rows = rows;
        Rows.ItemsSource = rows;
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.Value = string.Empty;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
