using System.Globalization;
using System.Text.RegularExpressions;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Data;

/// <summary>一条疑似合计行的命中：原表行号（0 起）+ 凭什么当它是合计行的人话理由。</summary>
/// <param name="RawRowIndex">原表行号（0 起）。报的是原表行号，与 <see cref="SheetLayoutChoice.ExcludedRawRows"/> 同一坐标系。</param>
/// <param name="Reason">人话理由（「写着合计」「正好等于这一列加出来的总和」），状态栏与 ① 步要说给用户听的那一句。</param>
public sealed record SummaryRowHit(int RawRowIndex, string Reason);

/// <summary>
/// 合计行/小计行的本地兜底判据（第 24 棒，§十-A-13「真表 A34=155 被当一条货 → 多一张假唛头」的最小改法）。
/// <para><strong>宁漏勿错杀</strong>：漏掉一条合计行只是多印一张有人能看见的纸，
/// 错杀一条货行是少印一箱货——所以只认两种几乎不可能冤枉好行的形状，判据放宽一寸之前先想这个不对称。</para>
/// <list type="bullet">
///   <item>信号 a：行内任一格写着 合计/总计/小计/TOTAL/SUBTOTAL，且全行至多一个数值格——
///   一箱货不会在自己身上写「合计」，写了也不该只剩一个数；</item>
///   <item>信号 b：全行只剩一个孤零零的数值格（其余全空），且与同列其余数据行加出来的总和差在容差内
///   （绝对差 ≤1 或相对差 ≤0.5%）——
///   「光秃秃一个数、约等于整列之和」是表尾合计最典型的形状（金沐 A34=155 精确命中；
///   TOP 表尾 578 与实和 579 差 1 件，靠容差才命中，导入层第 1 棒）。</item>
/// </list>
/// <para>与 AI 提案的分工（第 21 棒「谁来判断」的口径不变）：这里只做默认档兜底，
/// 剔了谁、凭什么逐行报出来（<see cref="DetectionResult.AutoSkippedSummaryRows"/>），① 步一键可关；
/// AI 或用户点名的剔除行走 <see cref="SheetLayoutChoice.ExcludedRawRows"/>，两边取并集、互不覆盖。</para>
/// </summary>
public static class SummaryRowSpotter
{
    /// <summary>合计字样的判据词（先剥空白再比，大小写不敏感）。</summary>
    private static readonly string[] LabelWords = { "合计", "总计", "小计", "subtotal", "total" };

    /// <summary>
    /// 在表头以下找疑似合计行。<paramref name="headerIndex"/> 为 -1（按指令没表头）时从第 0 行起扫。
    /// 返回按行号升序；不修改任何数据，剔不剔由调用方（<see cref="HeaderRowDetector"/>）按指令决定。
    /// </summary>
    public static IReadOnlyList<SummaryRowHit> Find(IReadOnlyList<string[]> grid, int headerIndex)
    {
        var hits = new List<SummaryRowHit>();
        var first = Math.Max(0, headerIndex + 1);
        if (first >= grid.Count) return hits;

        for (var r = first; r < grid.Count; r++)
        {
            var cells = NonEmptyCells(grid[r]);
            if (cells.Length == 0) continue;   // 整行空是检测层本来就跳过的空行，不算合计

            var numericCount = cells.Count(c => TryNumber(c.Text, out _));

            // 信号 a：写着合计字样，且全行至多一个数值格
            var labelText = cells.Select(c => c.Text).FirstOrDefault(t => HasLabelWord(t)) ?? string.Empty;
            if (labelText.Length > 0 && numericCount <= 1)
            {
                hits.Add(new SummaryRowHit(r, $"写着「{Trim(labelText)}」，不像一箱货"));
                continue;
            }

            // 信号 b：全行只剩一个孤立数值格（其余全空），且与同列其余数据行之和差在容差内
            if (cells.Length != 1 || numericCount != 1) continue;
            if (!TryNumber(cells[0].Text, out var value) || value <= 0) continue;
            var col = cells[0].Column;
            if (col >= grid[r].Length) continue;
            // 表尾门槛一度加过又撤了（导入层第 1 棒，单测当场抓的）：合计行后面还能跟一行批注
            // （金沐、OLU 真表就是这个形状），要求"必须是最后一行有内容"反而把合计行漏掉。
            // 防错杀改由容差自己守住：见下面 tolerance 的取法。
            double sum = 0;
            var others = 0;
            // 求和范围 = 全表除表头那一行（列名格不是数，且表头在末尾时货在它**上面**——
            // 只下表头之下求和的话，TOP 那种表的基数恒为 0，表尾总数永远对不上账）。
            for (var k = 0; k < grid.Count; k++)
            {
                if (k == r || k == headerIndex) continue;
                var row = grid[k];
                var cellText = col < row.Length ? row[col] ?? string.Empty : string.Empty;
                if (cellText.Trim().Length == 0) continue;
                if (NonEmptyCells(row).Any(c => HasLabelWord(c.Text))) continue;   // 别把另一条合计行也加进基数
                if (TryNumber(cellText, out var v)) { sum += v; others++; }
            }

            // 容差（导入层第 1 棒，取代原来的"分毫必须相等"）：TOP 那张表尾写着 578，
            // 而件数列 409 行实和是 579——差 1 件。工厂改过某行没回头改尾数是常态不是异常，
            // 原判据因此漏掉这一行，于是多印一张写着 578 的废纸。
            // 「绝对差 ≤1 或相对差 ≤0.5%」：件数 578/579 这种一位数的对不上账能容住；
            // 而 50+51+52=153、某行光秃秃写着 150（差 3、占 2%）这类**真货行**不会被冤枉——
            // 容差放宽一寸，错杀的就是少印的一箱货（本文件开头那条不对称）。
            var tolerance = Math.Max(1.0, sum * 0.005);
            if (others > 0 && Math.Abs(sum - value) <= tolerance)
            {
                var diff = Math.Abs(sum - value);
                var reason = diff < 0.0001
                    ? $"就孤零零一个数 {Trim(cells[0].Text)}，正好等于 {ColumnLabel(col)} 列其余 {others} 行加出来的总和"
                    : $"就孤零零一个数 {Trim(cells[0].Text)}，{ColumnLabel(col)} 列其余 {others} 行加起来是 {Plain(sum)}，" +
                      $"和它差 {Plain(diff)}——表尾这个数对不上账，不像一条货";
                hits.Add(new SummaryRowHit(r, reason));
            }
        }
        return hits;
    }

    /// <summary>给人看的数：整数不带小数点，小数原样（显式 InvariantCulture，§五-121）。</summary>
    private static string Plain(double value)
        => value % 1 == 0
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);

    private static (int Column, string Text)[] NonEmptyCells(string[] row)
    {
        var list = new List<(int, string)>(row.Length);
        for (var c = 0; c < row.Length; c++)
        {
            if (!string.IsNullOrWhiteSpace(row[c])) list.Add((c, row[c]));
        }
        return list.ToArray();
    }

    private static bool HasLabelWord(string text)
    {
        var compact = Regex.Replace(text, @"\s+", "").ToLowerInvariant();
        return LabelWords.Any(w => compact.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>整格就是一个数（允许千分位）才算数值格——「5件」「12 CBM」这类带单位的不算，宁漏勿错杀。</summary>
    private static bool TryNumber(string text, out double value)
        => double.TryParse(NumericText.WithoutThousandsSeparators(text.Trim()),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Trim(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    private static string ColumnLabel(int zeroBasedColumn) => HeaderRowDetector.ColumnLetter(zeroBasedColumn);
}
