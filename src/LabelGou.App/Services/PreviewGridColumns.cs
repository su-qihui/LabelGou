using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Data;

namespace LabelGou.App.Services;

/// <summary>
/// ① 步「表格显示区」那张网格的列生成。
/// <para><strong>为什么要有这个文件</strong>：网格用 <c>AutoGenerateColumns</c>，WPF 于是把
/// <strong>列名当绑定路径</strong>去解析 —— 而 <c>/</c> 在 PropertyPath 里是 DataViewManager 的分隔符、
/// <c>.</c> 是属性分隔符。表头写「张数/一开四」（店里 9.21 肖有、谭聪 9.19、谢通程 9.18 三张真表都这么写）
/// 时那一列<strong>整列取不到值</strong>：列名在、每一格都是空的，可 ②③④ 步按下标与整串比较取数照旧正常，
/// 打印也照旧对 —— 用户 2026-09-19 看到的正是"这一列消失了却还能正常读取打印"。</para>
/// <para>修法<strong>不去猜转义规则</strong>（把 <c>[列名]</c> 拼进路径只是把雷换个埋法：`]` 怎么转义、
/// 尾随空格算不算名字的一部分，都是平台内部规矩）。改成路径留空 —— 绑定拿到的就是这一行 ——
/// 由 <see cref="RowCell"/> 按列名<strong>原样</strong>去取那一格。表头文字与 DataTable 的列名
/// 一个字都不动，下游那几条路（<c>GetCell(r,c)</c> 按下标、<c>IndexOfHeader</c> 整串比较）不受影响。</para>
/// </summary>
public static class PreviewGridColumns
{
    /// <summary>挂在 <c>DataGrid.AutoGeneratingColumn</c> 上：把自动生成的那一列换成"按列名取值"的绑定。</summary>
    public static void AutoGenerating(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.Column is DataGridBoundColumn bound)
            bound.Binding = new Binding { Converter = new RowCell(e.PropertyName) };
    }

    /// <summary>
    /// 一行里按<strong>列名</strong>取那一格。列名里有什么字符都不影响——这里没有路径解析这一步。
    /// <para>取不到列就返回空，不猜"是不是那一列"：① 步这张表是给人核对原表的，
    /// 把 B 列的值画到 C 列头上比空着更糟。</para>
    /// </summary>
    public sealed class RowCell : IValueConverter
    {
        /// <summary>要取的那一列的名字（与 DataTable 的列名逐字相同）。</summary>
        public string Header { get; }

        public RowCell(string header) => Header = header;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not DataRowView row) return null;
            var column = row.Row.Table.Columns[Header];
            if (column is null) return null;
            return row.Row[column] is DBNull ? null : row.Row[column];
        }

        // 这张网格 IsReadOnly="True"，往回写这条路不该被走到；真被走到就是有人把它改成可编辑了，
        // 那时该来问"写回哪一列"，而不是让转换器悄悄吞掉一次编辑。
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException("① 步的表格显示区只读：值从原表来，不回写。");
    }
}
