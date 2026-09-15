using System.Globalization;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Data;

/// <summary>
/// 表头行识别 + 表头归一化。
/// <para>
/// 工厂发来的表极少"第一行就是干净表头"：常见前导标题行、空行、多行复合表头，
/// 也有整张表把列名写在<strong>最后一行</strong>的（TOP：411 行，列名在 r410）。
/// 因此对<strong>全表每一行</strong>按字段别名命中数为主、非空覆盖率与"像不像标签"为辅打分，
/// 选出最可能是表头的那一行。<strong>其上方的行原样留着</strong>（第 20 棒改）：
/// 工厂爱把「纸规 280×200」「外箱尺寸 60×40×30」「共 155 件」这类话写在前几行，
/// 以前一律丢弃，于是 AI 想看也看不到，人想知道它凭什么猜也无处查。
/// 这些行仍然<strong>不参与字段映射、不出标签</strong>，只是从「扔掉」变成「摊出来」。
/// </para>
/// </summary>
public static class HeaderRowDetector
{
    /// <summary>
    /// 行号惩罚的封顶（单位：分）。
    /// <para><strong>为什么必须有这个顶</strong>：打分原本只扫前 8 行，<c>-rowIndex * 0.15</c> 最多扣到 1.05 分，
    /// 作用仅是"同分时偏好在上的行"。导入层第 1 棒把扫描窗扩到<strong>全表</strong>之后，这条线性惩罚
    /// 会把表尾的真表头直接压死——TOP 那张 411 行表的列名写在<strong>第 410 行</strong>，按原式要吃 −61.5 分，
    /// 而它第一行是货（+2.86 分），于是永远认不回来。封顶后：前 20 行之内仍按原斜率偏好靠上的行，
    /// 再往下不再累加——同分依然偏上，但不至于把真表头挤出赛场。</para>
    /// </summary>
    private const double RowIndexPenaltyCap = 3.0;

    /// <summary>识别结果。</summary>
    /// <param name="HeaderRowIndex">表头所在行（0 起）；<b>-1 = 用户说这张表没表头</b>。</param>
    /// <param name="Headers">归一化后的表头（空标题补成 "列 A"，重复标题加序号）。</param>
    /// <param name="DataRows">表头之下的数据行（已按表头列宽补齐、已去掉整行空行与被剔除行）。</param>
    /// <param name="PreambleRows">表头<strong>以上</strong>那几行原样（含空行，行号就是原始网格行号），给 AI 与人看批注用。</param>
    /// <param name="DataRowRawIndexes">每一条数据行在<strong>原表</strong>里的行号（0 起），与 <paramref name="DataRows"/> 同序。
    /// <para>为什么要它：AI 报的是原表行号（它看到的就是原表），软件内部数的是第几条，
    /// 中间隔着一段被跳掉的行与剔掉的行——不留这张对应表，两边一定会错配（错配 = 剔错行）。</para></param>
    /// <param name="RawRowCount">原表总行数（含被跳过的与被剔除的）：AI 报行号的合法上界。</param>
    /// <param name="AutoSkippedSummaryRows">被合计行兜底剔掉的行（第 24 棒）：只含「纯靠判据剔」的那些，
    /// 被指令点名过的行不重复报。空表 = 没剔过任何东西；递 null 指令的旧调用方恒为空。</param>
    public sealed record DetectionResult(
        int HeaderRowIndex,
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<string>> DataRows,
        IReadOnlyList<IReadOnlyList<string>> PreambleRows,
        IReadOnlyList<int> DataRowRawIndexes,
        int RawRowCount = 0,
        IReadOnlyList<SummaryRowHit> AutoSkippedSummaryRows = null!);

    public static DetectionResult Detect(IReadOnlyList<string[]> grid) => Detect(grid, null);

    /// <summary>
    /// 按一份指令切表（第 21 棒）。<paramref name="choice"/> 为 null 或全默认时行为与以前一字不差。
    /// </summary>
    public static DetectionResult Detect(IReadOnlyList<string[]> grid, SheetLayoutChoice? choice)
    {
        if (grid.Count == 0)
            return new DetectionResult(0, Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>(),
                Array.Empty<IReadOnlyList<string>>(), Array.Empty<int>(), 0, Array.Empty<SummaryRowHit>());

        var width = grid.Max(r => r.Length);
        var hasHeader = choice?.HasHeader ?? true;
        var explicitExcluded = choice?.ExcludedRawRows is { Count: > 0 }
            ? new HashSet<int>(choice.ExcludedRawRows) : null;

        int headerIndex;
        if (!hasHeader) headerIndex = -1;
        else if (choice?.HeaderRowIndex is int forced)
            headerIndex = Math.Clamp(forced, 0, Math.Max(0, grid.Count - 1));   // 指到最后一行之内的合法位置
        else headerIndex = BestHeaderIndex(grid, width);

        var headerRow = headerIndex >= 0 ? grid[headerIndex] : Array.Empty<string>();
        var headers = NormalizeHeaders(headerRow, width);
        var firstDataRow = Math.Max(0, headerIndex + 1);

        // 合计行兜底（第 24 棒）：只在递了指令时生效（递 null = 旧行为一字不差）；
        // 表头那一行永远不在候选里（扫描从它下面开始），指令点名的剔除行与它取并集、不互相覆盖。
        var autoHits = choice is not null && choice.SkipSummaryRows
            ? SummaryRowSpotter.Find(grid, headerIndex)
            : Array.Empty<SummaryRowHit>();
        HashSet<int>? excluded = null;
        if (explicitExcluded is not null) excluded = new HashSet<int>(explicitExcluded);
        foreach (var hit in autoHits)
        {
            if (explicitExcluded is not null && explicitExcluded.Contains(hit.RawRowIndex)) continue;
            (excluded ??= new HashSet<int>()).Add(hit.RawRowIndex);
        }
        // 递 null 指令的旧路径：autoHits 恒空 → 这里恒空表，绝不把 null 递进非空属性
        IReadOnlyList<SummaryRowHit> autoReported = autoHits
            .Where(h => explicitExcluded is null || !explicitExcluded.Contains(h.RawRowIndex))
            .ToList();

        var preamble = new List<IReadOnlyList<string>>(Math.Max(0, headerIndex));
        var dataRows = new List<IReadOnlyList<string>>(Math.Max(0, grid.Count - firstDataRow));
        var rawIndexes = new List<int>(dataRows.Capacity);

        // 数据在表头<strong>之上</strong>（TOP 形状：411 行表把列名写在第 410 行，货全在上面）。
        // 不补这一支的话，认出列名反而切出一张零行的表——比认错表头还没用。
        // 条件刻意收窄：表头之上至少两行有内容、表头之下再没有没被剔的行。
        // 普通工厂表（表头在上、前几行是批注）走下面那条原路，行为与第 20 棒起一字不差。
        var aboveContent = CountContentRows(grid, 0, Math.Max(0, headerIndex), excluded);
        var belowContent = CountContentRows(grid, firstDataRow, grid.Count, excluded);
        var dataAboveHeader = headerIndex >= 0 && aboveContent >= 2 && belowContent == 0;

        if (dataAboveHeader)
        {
            for (var r = 0; r < grid.Count; r++)
            {
                if (r == headerIndex) continue;
                var row = grid[r];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                if (excluded is not null && excluded.Contains(r)) continue;
                dataRows.Add(Pad(row, width));
                rawIndexes.Add(r);
            }
            return new DetectionResult(headerIndex, headers, dataRows, preamble, rawIndexes, grid.Count, autoReported);
        }

        for (var r = 0; r < headerIndex; r++) preamble.Add(Pad(grid[r], width));   // 到表头那一行为止（不含它本身）

        for (var r = firstDataRow; r < grid.Count; r++)
        {
            var row = grid[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            if (excluded is not null && excluded.Contains(r)) continue;
            dataRows.Add(Pad(row, width));
            rawIndexes.Add(r);
        }

        // HeaderRowIndex == -1 本身就是「没表头」的记号，不再另存一个 bool（两个真源必有一个会说谎）。
        // RawRowCount 递的是原表总行数（含被剔的）：下一轮 AI 报行号时边界必须是这张表本来的长度，
        // 否则剔过几行之后，「第 412 行」会被当成越界误删——而合计行恰恰就在最后那几行。
        return new DetectionResult(headerIndex, headers, dataRows, preamble, rawIndexes, grid.Count, autoReported);
    }

    /// <summary>数 [from, to) 这段行里「有内容且没被点名剔除」的行数。</summary>
    private static int CountContentRows(
        IReadOnlyList<string[]> grid, int from, int to, HashSet<int>? excluded)
    {
        var n = 0;
        for (var r = Math.Max(0, from); r < Math.Min(to, grid.Count); r++)
        {
            if (excluded is not null && excluded.Contains(r)) continue;
            if (grid[r].Any(c => !string.IsNullOrWhiteSpace(c))) n++;
        }
        return n;
    }

    /// <summary>把一行补齐到表宽（Excel 的稀疏行右边那几格根本不存在，不是空串）。</summary>
    private static string[] Pad(string[] row, int width)
    {
        if (row.Length == width) return row;
        var padded = new string[width];
        Array.Copy(row, padded, Math.Min(row.Length, width));
        for (var c = row.Length; c < width; c++) padded[c] = string.Empty;
        return padded;
    }

    /// <summary>
    /// 给<strong>全表每一行</strong>打分，取最高分那行当表头。
    /// <para>为什么不再只扫前几行（导入层第 1 棒）：工厂表的列名不总在上面——TOP 那张 411 行表
    /// 把列名写在最后一行（r410），上面 409 行全是货。只扫前 8 行时软件会把 r1 当表头，
    /// 结果 409 行全部绑不上字段，而真表头那一行反倒被当成一条货印出来。</para>
    /// </summary>
    private static int BestHeaderIndex(IReadOnlyList<string[]> grid, int width)
    {
        var bestIndex = 0;
        var bestScore = double.MinValue;
        for (var i = 0; i < grid.Count; i++)
        {
            var score = Score(grid[i], width, i);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    /// <summary>
    /// 给一行"像不像表头"打分。命中越多标准字段别名，权重越高——这是最强信号。
    /// </summary>
    private static double Score(string[] row, int width, int rowIndex)
    {
        var nonEmpty = row.Count(c => !string.IsNullOrWhiteSpace(c));
        if (nonEmpty == 0) return double.MinValue;

        var coverage = (double)nonEmpty / width;              // 非空覆盖率
        var labelish = row.Count(c => LooksLikeLabel(c));      // 短、非纯数字
        var aliasHits = row.Count(c => AliasHit(c));           // 命中唛头字段别名

        var score = 0d;
        score += coverage * 3.0;
        score += (double)labelish / width * 2.0;
        score += Math.Min(aliasHits, 8) * 4.0;                // 别名命中主导
        score -= Math.Min(rowIndex * 0.15, RowIndexPenaltyCap);   // 同等条件偏好在上的行，但扣到顶就停（见 RowIndexPenaltyCap）
        if (nonEmpty < 2) score -= 5;                         // 只有一格基本不是表头
        return score;
    }

    private static bool LooksLikeLabel(string cell)
    {
        var text = cell?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > 24) return false;
        // 纯数字/日期更像数据而非表头
        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return false;
        return true;
    }

    /// <summary>
    /// 全部别名的归一化集合（<strong>归一化口径与 <see cref="Normalize"/> 同一个函数</strong>，两处各写一份必走样）。
    /// <para>为什么要收合成一张表：扫描窗扩到全表之后，每一行的每一格都要问一次"这是不是列名"。
    /// 原来每次问都要把整份字段目录连别名重扫一遍（411 行 × 7 格 × 全目录），长表上是纯浪费。</para>
    /// </summary>
    internal static readonly Lazy<HashSet<string>> AliasKeys = new(() =>
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in MarkFieldCatalog.All)
        {
            set.Add(Normalize(def.ChineseName));
            if (def.EnglishLabel.Length > 1) set.Add(Normalize(def.EnglishLabel));
            foreach (var alias in def.Aliases) set.Add(Normalize(alias));
        }
        set.Remove(string.Empty);
        return set;
    });

    /// <summary>
    /// 这一格是不是一个标准字段名：<strong>整格命中，或它的任一分段命中</strong>都算。
    /// <para>为什么要分段（导入层第 1 棒实测）：真表的列名普遍一格写中英两行——
    /// <c>件数⏎CTN</c>、<c>货号⏎ITEM NO:</c>、<c>装件数⏎PCS/CTN</c>。<see cref="Normalize"/> 刻意删掉所有空白，
    /// 于是「件数⏎CTN」粘成「件数ctn」，词典里没这个词 → 整张表一个别名都不命中。
    /// TOP 那张 411 行表的真表头写在最后一行，就是靠「件数」「ctn」这两段才被认出来的。</para>
    /// <para>口径仍是<strong>精确相等</strong>，不做包含匹配——包含会抢列（§五-66）。</para>
    /// </summary>
    public static bool IsKnownFieldName(string? cell)
    {
        var text = cell?.Trim() ?? string.Empty;
        if (text.Length == 0) return false;
        if (AliasKeys.Value.Contains(Normalize(text))) return true;
        foreach (var segment in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (AliasKeys.Value.Contains(Normalize(segment))) return true;
        }
        return false;
    }

    /// <summary>该文本（或分段）是否命中某个标准字段的别名。</summary>
    private static bool AliasHit(string cell) => IsKnownFieldName(cell);

    /// <summary>归一化表头：去空白、去内部空格与常见符号，便于匹配与展示。</summary>
    public static string Normalize(string? text)
    {
        var raw = text?.Trim() ?? string.Empty;
        if (raw.Length == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is '：' or ':' or '（' or '(' or '）' or ')' or '、' or '.' or '．' or '。' or '-' or '_' or '/') continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    private static IReadOnlyList<string> NormalizeHeaders(string[] headerRow, int width)
    {
        var result = new List<string>(width);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var c = 0; c < width; c++)
        {
            var raw = c < headerRow.Length ? headerRow[c].Trim() : string.Empty;
            var name = raw.Length == 0 ? "列 " + ColumnLetter(c) : raw;

            // 合并单元格跨列或重名时补序号，保证列标题唯一（映射界面按下标取列）
            if (seen.TryGetValue(name, out var count))
            {
                count++;
                seen[name] = count;
                name = $"{name} ({count})";
            }
            else
            {
                seen[name] = 1;
            }
            result.Add(name);
        }
        return result;
    }

    /// <summary>0→A，25→Z，26→AA。用于给无名列出个可读的字母名。</summary>
    public static string ColumnLetter(int zeroBasedColumn)
    {
        var n = zeroBasedColumn + 1;
        var sb = new System.Text.StringBuilder();
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            n = (n - 1) / 26;
        }
        return sb.ToString();
    }
}
