namespace LabelGou.Core.Marks;

/// <summary>
/// 列名的「单行显示形」。<strong>值</strong>永远用表头原样（精确匹配才认得出是哪一列），
/// 只有<em>给人看</em>的地方折行。
/// <para>为什么需要它：真表的表头格子里常常带换行——金沐那张就是「件数(换行)CTN」、「数量(换行)QTY」，
/// 直接塞进下拉项会把一行选项劈成两行，塞进告警会把一条问题劈成三条（第 14 棒探针在真表上撞到的）。</para>
/// </summary>
public static class ColumnLabel
{
    /// <summary>把列名里的换行折成「 / 」，并压掉首尾与段间多余空白。空/ null → 空串。</summary>
    public static string SingleLine(string? column)
    {
        if (string.IsNullOrWhiteSpace(column)) return string.Empty;

        var normalized = column!.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.IndexOf('\n') < 0) return column.Trim();

        var parts = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" / ", parts);
    }
}
