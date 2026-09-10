using LabelGou.Core.Data;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 「标签上那几行字该多大、要不要粗、居不居中」——<strong>由软件照表格里量到的格式算</strong>，不再问模型（第 39 棒）。
/// <para>
/// <strong>为什么要有它</strong>：用户 2026-09-10 实测「AI 排版效果差，差在字体大小」，并自己判断出
/// 「AI 读不到表格中字体,粗细,居中等」。第 31 棒照这句把格式读出来了，可读出来之后做的事是
/// <em>把量到的数字压成几句大白话，再请模型把数字猜回来</em>——量测明明在软件手里，结论却要一个概率模型口算回填，
/// 所以每一轮都不一样。本棒把这三项从模型的输出里收回来（口径见活账 ai-17：关键数值必须软件计算）。
/// </para>
/// <para>
/// <strong>算法只有三步，每步都有出处</strong>：
/// ① 表里的<em>绝对</em>字号没有意义（Excel 里 11pt 跟 140mm 的标签不相干），有意义的只是<strong>比例</strong>：
///    最小的那行当 1，其余按倍数当行高权重。金沐那张验算——量到 [40,28,28,28] 就得权重 [1.43,1,1,1]，
///    与 <c>BuiltInTemplates.FourRows</c> 照真件写死的 [1.4,1,1,1] 是同一个数。
/// ② 绝对字号由行带几何反算，用的是 <see cref="RowLayoutSpec.StretchPointForBand"/>，
///    也就是房里那个「2.08 pt/mm」——它不是常数，是 <c>StretchEmPerBand(0.74) × MmToPoint(1mm)</c>。
/// ③ 粗体与居中跟几何无关，量到什么就是什么，<strong>原样盖掉</strong>模型填的。
/// </para>
/// <para>
/// <strong>这一条恒等式杀掉了「字体大小不统一」那一类缺陷</strong>：同一份量测里相等的字号 → 相等的权重 →
/// 相等的行带 → 相等的字号，是恒等式不是启发式。而"撑满"那行只许一行字、装不下就缩字号，
/// 两行都撑满就各缩各的（值长的反而比值短的小——那正是用户圈过的毛病），所以<strong>至多一行许撑满</strong>：
/// 量到的最大字号唯一才给它，并列就一行都不给。
/// </para>
/// <para>
/// <strong>量不到就一个字不改</strong>（不猜闸）：没说模板抄在哪一列、那一列没有写字的格、
/// 连着几行的行数跟标签行数对不上、有好几处都对不上号——一律保持模型填的值并写一句人话说明。
/// <em>宁可字号是它猜的，也不要软件按一个认错位置的块把字号改错。</em>
/// </para>
/// </summary>
public static class RowFormatEvidence
{
    /// <summary>
    /// 照 <paramref name="formats"/> 里 <paramref name="templateColumn"/> 那一列量到的格式，改写
    /// <paramref name="spec"/> 每一行的权重、字号、撑满标记、粗体与居中。<strong>就地改</strong>（这份 spec 是本轮刚解析出来的）。
    /// <para><strong>行内容与行序一律不碰</strong>：标签上印哪几行、什么顺序、每行读哪一列，那是模型认出来的活，本方法不抢。</para>
    /// </summary>
    /// <param name="spec">模型给的行式版式；null 或没有行时什么都不做。</param>
    /// <param name="formats">逐格量到的格式，来自 <see cref="XlsxTableReader.ReadCellFormats"/>；CSV 没有格式可言，传 null。</param>
    /// <param name="templateColumn">模板抄在哪一列（0 基）；<c>-1</c> = 模型没说或软件没对上。</param>
    /// <param name="notes">往里写给人看的一句人话（为什么改了 / 为什么没改）。</param>
    /// <param name="headerRowIndex">
    /// 列名在原表第几行（<strong>0 基</strong>）；<c>-1</c> = 不知道（模型没说，或这张表没有列名行）。
    /// <para>为什么要它：抄标签那块通常就<strong>贴在列名下面</strong>，连着读会把「1 格列名 + 4 行标签」
    /// 读成 5 行，跟标签的 4 行对不上，整块证据白量。而「列名在第几行」软件本来就知道，
    /// 拿它排掉那一格<strong>不是猜</strong>——猜的做法（比如"多出来的一律当列名扔掉"）会在
    /// 多出来的其实是脚注时把整块错位，那就成了悄悄给错字号，比不给更坏。</para>
    /// </param>
    /// <returns>真改动了 spec 里任何一项就是 true。</returns>
    public static bool TryApply(
        RowLayoutSpec? spec,
        IReadOnlyList<CellFormat>? formats,
        int templateColumn,
        List<string>? notes = null,
        int headerRowIndex = -1)
    {
        if (spec is null || spec.Rows.Count == 0) return false;
        if (formats is null || formats.Count == 0) return false;

        if (templateColumn < 0)
        {
            notes?.Add("标签上字多大、粗不粗、居不居中，这一版还是照它自己填的算：它没说模板抄在表里哪一列，软件没处去量");
            return false;
        }

        var where = HeaderRowDetector.ColumnLetter(templateColumn) + " 列";
        // 读取器只记写了字的格，所以「这一列的这些条按行号排好」就是那一列里写了字的那些格。
        // 落在列名行的那一格先排掉（见 headerRowIndex 的说明）。
        var cells = formats
            .Where(f => f.Col == templateColumn && f.Row != headerRowIndex)
            .OrderBy(f => f.Row)
            .ToList();
        if (cells.Count == 0)
        {
            notes?.Add($"{where}一个写了字的格都没量到格式，字号还是照它自己填的算");
            return false;
        }

        var runs = SplitRuns(cells);
        var matched = runs.Where(r => r.Count == spec.Rows.Count).ToList();
        if (matched.Count == 0)
        {
            notes?.Add($"{where}量到写了字的是{DescribeRuns(runs)}，跟它给的 {spec.Rows.Count} 行标签对不上——" +
                       "字号没敢照量到的改，还是它自己填的");
            return false;
        }
        if (matched.Count > 1)
        {
            notes?.Add($"{where}有 {matched.Count} 处都是连着 {spec.Rows.Count} 行写了字的，认不出哪一处是抄标签那块——" +
                       "字号还是照它自己填的算");
            return false;
        }

        var block = matched[0];
        var rows = spec.Rows;

        // 粗体与居中：跟几何无关，量到什么就是什么，模型填的那两个一个都不留。
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Bold = block[i].Bold;
            rows[i].Align = RowLayoutJsonParser.ParseAlign(block[i].Align);
        }

        var sizes = block.Select(c => c.SizePt).ToArray();
        var measured = sizes.Where(s => s > 0).ToList();
        if (measured.Count == 0)
        {
            notes?.Add($"{where}那 {block.Count} 行量到了粗细与居中，字号量不到（这份表里没写字体表）——字号还是照它自己填的算");
            return true;
        }

        var minSize = measured.Min();
        var maxSize = measured.Max();
        var unmeasured = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            if (sizes[i] <= 0) { unmeasured++; continue; }
            rows[i].Weight = Math.Clamp(sizes[i] / minSize, 0.05, 8);
        }

        var biggest = sizes.Select((s, i) => (s, i)).Where(t => t.s == maxSize).ToList();
        var stretchIndex = biggest.Count == 1 ? biggest[0].i : -1;

        var bands = spec.BandHeightsMm();
        if (bands.Count != rows.Count)
        {
            notes?.Add($"{where}的字量到了，行高比例也照它改了，但这张标签排不出行带来（留白或行距把版面吃光了），字号没能跟着改");
            return true;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Stretch = i == stretchIndex;
            if (sizes[i] <= 0) continue;                 // 这一行没量到字号，保留它原来填的
            rows[i].SizePt = RowLayoutSpec.StretchPointForBand(bands[i]);
        }

        var sizeLine = maxSize > minSize
            ? $"最大那行是其余的 {maxSize / minSize:0.#} 倍"
            : "几行字一样大";
        notes?.Add($"标签上那 {rows.Count} 行字的大小、粗细、居中，是软件从{where}那几行字上量出来算的（{sizeLine}），不是它填的"
            + (unmeasured > 0 ? $"；有 {unmeasured} 行量不到字号，那几行还是照它填的" : string.Empty));
        return true;
    }

    /// <summary>把一列里写了字的格按「行号连着」切成若干块（抄标签那块通常是连着几行）。</summary>
    private static List<List<CellFormat>> SplitRuns(List<CellFormat> cells)
    {
        var runs = new List<List<CellFormat>>();
        var lastRow = int.MinValue;
        foreach (var c in cells)
        {
            if (runs.Count == 0 || c.Row != lastRow + 1) runs.Add(new List<CellFormat>());
            runs[^1].Add(c);
            lastRow = c.Row;
        }
        return runs;
    }

    /// <summary>把几块说成人话（行号一律 1 起，与人看 Excel 的口径一致）。</summary>
    private static string DescribeRuns(List<List<CellFormat>> runs)
    {
        var parts = runs.Take(6).Select(r =>
        {
            var from = r[0].Row + 1;
            var to = r[^1].Row + 1;
            return from == to ? $"第 {from} 行的 1 行" : $"第 {from}~{to} 行的 {r.Count} 行";
        });
        var text = string.Join("、", parts);
        return runs.Count > 6 ? text + $" 等 {runs.Count} 处" : text;
    }
}
