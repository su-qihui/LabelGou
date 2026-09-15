using System.Globalization;
using System.Text.RegularExpressions;

namespace LabelGou.Core.Data;

/// <summary>一级校验每条发现的档位。</summary>
public enum HealthLevel
{
    /// <summary>软件已经按判据自动处理了，只说一声（含"凭什么"）。</summary>
    Notice,
    /// <summary>发现了一个可执行的调整——一键修复会带上它。</summary>
    Suggestion,
    /// <summary>有问题，但软件没有足够事实去改它：只报，不猜。</summary>
    Warning,
}

/// <summary>发现的种类（界面按它分组，日志按它检索）。</summary>
public enum HealthKind
{
    HeaderRowChosen, HeaderRowUncertain,
    SummaryRowSkipped, SummaryRowNotSkipped,
    ColumnWithoutHeader, HeaderIsInstruction,
    DuplicateStarTail,
    SideTextTemplate, SideInstruction,
    BarcodeColumn,
    EmbeddedImagePlaceholder,
    MultipleValuesInOneCell,
    SheetWithoutData, MultipleSheetsWithData,
}

/// <summary>右侧稀疏块的身份证：厂方写在数据区右侧的那一列到底是什么。</summary>
public enum SideBlockKind
{
    /// <summary>
    /// 表内抄录的<strong>文字模板</strong>（如邱总 F 列 <c>JP / ITEM：香水 perfume / ITEM NO：aym6101 / QTY：96 PCS</c>）。
    /// <para>它不是脏数据（用户 2026-09-13 明确纠正过这一点）：处置是「从数据行圈出去 + 原样保住交给版式层当参照」，
    /// 绝不是删。它是「无参照不出模板」那条规矩的参照来源。</para>
    /// </summary>
    TextTemplate,
    /// <summary>给操作者的<strong>指令</strong>（<c>一开四</c>、<c>张数等于件数</c>、<c>一个流水号一张</c>、<c>货号写客户的8位货号</c>）。</summary>
    Instruction,
}

/// <summary>一列被认成右侧块的结果。</summary>
/// <param name="Column">列号（0 起）。</param>
/// <param name="Kind">文字模板还是指令。</param>
/// <param name="RawRows">出现过的原表行号（0 起）。</param>
/// <param name="Sample">头两格原样，给人核对用（不许编）。</param>
public sealed record SideBlock(int Column, SideBlockKind Kind, IReadOnlyList<int> RawRows, string Sample);

/// <summary>一条可执行的调整。App 层照它落，不许自己另发明一套落地路。</summary>
public enum HealthFixKind { MoveHeaderRow, ExcludeSummaryRows, MarkSideColumns, AddValueRules }

/// <param name="Kind">改哪一类。</param>
/// <param name="HeaderRowIndex"><see cref="HealthFixKind.MoveHeaderRow"/> 时：列名改到第几行（0 起）。</param>
/// <param name="Rows"><see cref="HealthFixKind.ExcludeSummaryRows"/> 时：要剔的原表行号（0 起）。</param>
/// <param name="SideBlocks"><see cref="HealthFixKind.MarkSideColumns"/> 时：圈出去的列及其身份。</param>
/// <param name="Rules"><see cref="HealthFixKind.AddValueRules"/> 时：要追加的值改写规则。</param>
public sealed record HealthFix(
    HealthFixKind Kind, int HeaderRowIndex = -1,
    IReadOnlyList<int>? Rows = null, IReadOnlyList<SideBlock>? SideBlocks = null,
    IReadOnlyList<ValueRule>? Rules = null)
{
    /// <summary>把这条调整合到现有切表指令上（只读入参、返回新的一份——指令是不可变的）。</summary>
    public SheetLayoutChoice Apply(SheetLayoutChoice current) => Kind switch
    {
        HealthFixKind.MoveHeaderRow => current with { HeaderRowIndex = HeaderRowIndex, HasHeader = true },
        HealthFixKind.ExcludeSummaryRows => current with
        {
            ExcludedRawRows = MergeRows(current.ExcludedRawRows, Rows),
        },
        HealthFixKind.MarkSideColumns => current with
        {
            SideBlocks = MergeBlocks(current.SideBlocks, SideBlocks),
        },
        HealthFixKind.AddValueRules => current with
        {
            ValueRules = MergeRules(current.ValueRules, Rules),
        },
        _ => current,
    };

    private static IReadOnlyList<ValueRule> MergeRules(IReadOnlyList<ValueRule>? existing, IReadOnlyList<ValueRule>? added)
    {
        var list = new List<ValueRule>(existing ?? Array.Empty<ValueRule>());
        foreach (var r in added ?? Array.Empty<ValueRule>())
        {
            if (!list.Any(x => x.Kind == r.Kind && x.TargetColumn == r.TargetColumn && x.SourceColumn == r.SourceColumn))
                list.Add(r);
        }
        return list;
    }

    private static IReadOnlyList<int> MergeRows(IReadOnlyList<int>? existing, IReadOnlyList<int>? added)
    {
        var set = new HashSet<int>(existing ?? Array.Empty<int>());
        foreach (var r in added ?? Array.Empty<int>()) set.Add(r);
        return set.OrderBy(x => x).ToList();
    }

    private static IReadOnlyList<SideBlock> MergeBlocks(IReadOnlyList<SideBlock>? existing, IReadOnlyList<SideBlock>? added)
    {
        var byCol = new Dictionary<int, SideBlock>();
        foreach (var b in existing ?? Array.Empty<SideBlock>()) byCol[b.Column] = b;
        foreach (var b in added ?? Array.Empty<SideBlock>()) byCol[b.Column] = b;   // 同一列以新判的为准
        return byCol.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }
}

/// <summary>一级校验的一条发现。</summary>
/// <param name="Kind">种类。</param>
/// <param name="Level">档位。</param>
/// <param name="Fact">一句人话：发现了什么（界面直接摆这句）。</param>
/// <param name="Evidence">凭什么：行号列号与算出来的数，不许含"建议优化"这类没有依据的话。</param>
/// <param name="RawRow">相关原表行号（0 起），可空。</param>
/// <param name="RawColumn">相关列号（0 起），可空。</param>
/// <param name="Fix">可执行的调整；null = 只报不修。</param>
public sealed record HealthFinding(
    HealthKind Kind, HealthLevel Level, string Fact, string Evidence,
    int? RawRow = null, int? RawColumn = null, HealthFix? Fix = null)
{
    /// <summary>给人看的一行（行号一律 1 起——人看的是 Excel 里的号，不是数组下标）。</summary>
    public string Describe()
    {
        var at = RawRow is int r ? $"（原表第 {r + 1} 行）" : string.Empty;
        return $"{Fact}{at} —— 凭什么：{Evidence}";
    }
}

/// <summary>
/// 导入后、绑定前的<strong>一级校验</strong>（导入层第 1 棒）：用确定性判据把"这张表会被切错的地方"扫一遍。
/// <para><strong>为什么这一层先由程序做、AI 在后</strong>：表头在第几行、哪个数是表尾总数、
/// 哪一列整列是 13 位条码——这些都能算出来，算出来的东西就该是铁证。
/// 交给概率模型报数，就是把"上游的猜测变成下游的事实"那条老路再走一遍（§五-100、第 39 棒铁律）。
/// AI 负责的是程序判不了的那三件：这列是什么语义、哪些行不该上纸、同一形状的尾巴留不留。</para>
/// <para><strong>纯逻辑</strong>：不碰网络、不碰 WPF、不改数据。它只产出发现，改不改由 App 层按
/// 「一键修复 / 逐条执行」决定，且全程可撤回。</para>
/// </summary>
public static class TableHealthCheck
{
    /// <summary>指令词（右侧稀疏列里出现这些，判为"给操作者看的话"，不是货、也不是版式样子）。</summary>
    private static readonly string[] InstructionWords =
    {
        "一开二", "一开四", "一开八", "大开二", "小开二", "开二", "开四",
        "四面贴", "正侧各一", "放到最大", "张数等于", "流水号一张", "写客户", "写我们", "另加",
    };

    /// <summary>标签式写法：`Item no：olu830-35`、`QTY：144 pcs`、`Ctns：5件`、`ITEM：香水 perfume`。</summary>
    private static readonly Regex LabelShape = new(
        @"^\s*[A-Za-z\u4e00-\u9fff][A-Za-z0-9 .#/_-]{0,15}\s*[:：]", RegexOptions.Compiled);

    /// <summary>一格两值：同一个「前缀：」出现两次，中间用 - 连着（郑 <c>QI YUE: AJ1-QI YUE: AJ3</c>，前缀本身带空格）。</summary>
    private static readonly Regex TwicePrefixed = new(
        @"^\s*([A-Za-z\u4e00-\u9fff][A-Za-z\u4e00-\u9fff ]{1,14}[:：])\s*\S+\s*-\s*\1",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>指令里点名来源列：「张数等于件数」→ 拿「件数」去列名里找。</summary>
    private static readonly Regex EqualsOtherColumn = new(@"等于\s*([^\s，。,;；：]{1,8})", RegexOptions.Compiled);

    /// <summary>WPS 塞进单元格的图：格值是 <c>=DISPIMG("ID_xxx",1)</c> 这样的占位文本。</summary>
    private const string DispImgToken = "=DISPIMG(";

    /// <summary>
    /// 扫一张已导入的表（界面层入口）：原表网格不在时如实返回空清单——拿切过的表扫会把行号报错。
    /// </summary>
    public static IReadOnlyList<HealthFinding> Scan(TabularData data)
    {
        if (data.RawGrid is not { Count: > 0 } grid) return Array.Empty<HealthFinding>();
        var cut = new HeaderRowDetector.DetectionResult(
            data.HeaderRowIndex, data.Headers, data.Rows, data.Preamble,
            data.DataRowRawIndexes, data.RawRowCount, data.AutoSkippedSummaryRows);
        return Scan(grid, cut, data.Choice.SideBlocks);
    }

    /// <summary>
    /// 扫一张表。<paramref name="grid"/> 是原始网格（未切），<paramref name="cut"/> 是软件当前的切法结果，
    /// <paramref name="sideBlocks"/> 是已经圈出去的右侧列（修过的不再当"可修"报第二遍）。
    /// </summary>
    public static IReadOnlyList<HealthFinding> Scan(
        IReadOnlyList<string[]> grid, HeaderRowDetector.DetectionResult cut,
        IReadOnlyList<SideBlock>? sideBlocks = null)
    {
        var findings = new List<HealthFinding>();
        if (grid.Count == 0) return findings;

        ScanHeader(grid, cut, findings);
        ScanSummaryRows(grid, cut, findings);
        ScanColumns(grid, cut, findings, sideBlocks);
        ScanCellShapes(grid, cut, findings);
        return findings;
    }

    /// <summary>各 sheet 的有数据行数（App 层从 <see cref="TableImporter"/> 那侧拿到后递进来）。</summary>
    public static IReadOnlyList<HealthFinding> ScanSheets(IReadOnlyList<(string Name, int DataRows)> sheets)
    {
        var list = new List<HealthFinding>();
        var withData = sheets.Where(s => s.DataRows > 0).ToList();
        foreach (var (name, _) in sheets.Where(s => s.DataRows == 0))
            list.Add(new HealthFinding(HealthKind.SheetWithoutData, HealthLevel.Warning,
                $"工作表「{name}」是空的", "整表没有一行有内容，选它会切出一张空表", null, null));
        if (withData.Count > 1)
            list.Add(new HealthFinding(HealthKind.MultipleSheetsWithData, HealthLevel.Warning,
                $"这张文件里有 {withData.Count} 张表都有数据，当前只导了其中一张",
                "有数据的分别是：" + string.Join("、", withData.Select(s => $"{s.Name}（{s.DataRows} 行）")),
                null, null));
        return list;
    }

    // ── K-1 列名在哪一行 ────────────────────────────────────────────────

    private static void ScanHeader(IReadOnlyList<string[]> grid, HeaderRowDetector.DetectionResult cut, List<HealthFinding> findings)
    {
        var headerRow = cut.HeaderRowIndex >= 0 && cut.HeaderRowIndex < grid.Count
            ? grid[cut.HeaderRowIndex] : Array.Empty<string>();
        var hits = headerRow.Count(HeaderRowDetector.IsKnownFieldName);

        if (hits == 0)
        {
            findings.Add(new HealthFinding(HealthKind.HeaderRowUncertain, HealthLevel.Warning,
                cut.HeaderRowIndex >= 0
                    ? $"软件把列名认在第 {cut.HeaderRowIndex + 1} 行，但那一格一个标准字段名都没命中"
                    : "整张表没找到任何一行像列名",
                "没有任何一格精确等于标准字段名或其别名；这一行当表头用的话，字段绑不上",
                cut.HeaderRowIndex >= 0 ? cut.HeaderRowIndex : null));
            return;
        }

        if (cut.HeaderRowIndex > 0)
        {
            var above = grid.Take(cut.HeaderRowIndex).Count(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
            // 前面只有一两行空行（金沐那种）不值得占面板一行；有批注行、或列名认得靠后才说。
            if (above > 0 || cut.HeaderRowIndex >= 3)
                findings.Add(new HealthFinding(HealthKind.HeaderRowChosen, HealthLevel.Notice,
                    $"列名认在第 {cut.HeaderRowIndex + 1} 行（不在第一行）",
                    above > 0
                        ? $"那一行 {hits} 格命中标准字段名；它上面还有 {above} 行有内容，已原样留着当批注，不参与映射、不出唛头"
                        : $"那一行 {hits} 格命中标准字段名（它上面是空行）",
                    cut.HeaderRowIndex));
        }
    }

    // ── K-2 表尾多余的总数行 ─────────────────────────────────────────────

    private static void ScanSummaryRows(IReadOnlyList<string[]> grid, HeaderRowDetector.DetectionResult cut, List<HealthFinding> findings)
    {
        var found = SummaryRowSpotter.Find(grid, cut.HeaderRowIndex);
        var skipped = new HashSet<int>(cut.AutoSkippedSummaryRows.Select(h => h.RawRowIndex));

        foreach (var hit in cut.AutoSkippedSummaryRows)
            findings.Add(new HealthFinding(HealthKind.SummaryRowSkipped, HealthLevel.Notice,
                $"第 {hit.RawRowIndex + 1} 行为合计数值，已删除", hit.Reason, hit.RawRowIndex));

        var leftovers = found.Where(h => !skipped.Contains(h.RawRowIndex)).ToList();
        if (leftovers.Count > 0)
            findings.Add(new HealthFinding(HealthKind.SummaryRowNotSkipped, HealthLevel.Suggestion,
                $"第 {string.Join("、", leftovers.Select(h => (h.RawRowIndex + 1).ToString(CultureInfo.InvariantCulture)))} 行为合计数值，删除",
                "凭什么：" + string.Join("；", leftovers.Select(h => $"第 {h.RawRowIndex + 1} 行：{h.Reason}")) +
                "。留着它就多印一张废纸",
                leftovers[0].RawRowIndex, null,
                new HealthFix(HealthFixKind.ExcludeSummaryRows, Rows: leftovers.Select(h => h.RawRowIndex).ToList())));
    }

    // ── K-3 / K-5 / K-7 按列看 ───────────────────────────────────────────

    private static void ScanColumns(IReadOnlyList<string[]> grid, HeaderRowDetector.DetectionResult cut,
        List<HealthFinding> findings, IReadOnlyList<SideBlock>? sideBlocks)
    {
        var width = grid.Max(r => r.Length);
        var dataRows = cut.DataRowRawIndexes;
        if (dataRows.Count == 0) return;

        for (var c = 0; c < width; c++)
        {
            var values = new List<(int Row, string Text)>();
            foreach (var r in dataRows)
            {
                var text = (c < grid[r].Length ? grid[r][c] : null)?.Trim() ?? string.Empty;
                if (text.Length > 0) values.Add((r, text));
            }
            var header = c < cut.Headers.Count ? cut.Headers[c].Trim() : string.Empty;
            var letter = HeaderRowDetector.ColumnLetter(c);
            // 右侧块先判：它比"没列名"更具体（说清了那一列到底是什么）。
            // 判出来了这一列就只报这一条——同一列报两遍说的是同一件事，面板会被自己的话挤乱（用户 2026-09-13 实测）。
            var block = values.Count >= 1 && values.Count <= Math.Max(4, dataRows.Count / 4)
                ? ClassifySideBlock(c, values)
                : null;

            // K-3：有数据却没列名（OLU 的 M 列装着外箱尺寸，列名行只到 L）
            if (block is null && values.Count > 0
                && (header.Length == 0 || header.StartsWith("列 ", StringComparison.Ordinal)))
                findings.Add(new HealthFinding(HealthKind.ColumnWithoutHeader, HealthLevel.Warning,
                    $"{letter} 列有 {values.Count} 行数据，却没列名",
                    $"软件补的名字是「{header}」；第一格原样「{Clip(values[0].Text)}」（第 {values[0].Row + 1} 行）——" +
                    "这种列最容易被漏绑，去 ② 步手工连或点名让 AI 认",
                    values[0].Row, c));

            // K-7：整列是纯数字 → 到底是不是条码，拿校验位判，不拿长度猜。
            //      TOP 的 A 列（客户的 8 位货号）长度也像条码，可校验位只有约十分之一对得上（纯概率命中）；
            //      E 列 409 格 13 位则全部对得上 —— 那才是条码。
            //      "像条码但校验位大面积对不上"不在这里报：③ 步编码本来就会拒收，报两遍是噪音。
            var digitish = values.Where(v => Regex.IsMatch(v.Text, @"^\d{8,14}$")).ToList();
            if (values.Count >= 3 && digitish.Count >= values.Count * 0.8)
            {
                var pass = digitish.Count(v => GtinCheckOk(v.Text));
                if (pass >= digitish.Count * 0.8)
                    findings.Add(new HealthFinding(HealthKind.BarcodeColumn, HealthLevel.Notice,
                        $"{letter} 列整列是 {digitish.Count} 个纯数字，且校验位对得上——这是条码，不是数量",
                        $"长度 {string.Join("/", digitish.Select(v => v.Text.Length).Distinct().OrderBy(x => x))}；" +
                        $"{pass}/{digitish.Count} 个的最后一位与 GTIN 模 10 校验位吻合。" +
                        "这列绝不能当数值格式化（会变 6.93666E+12 那种废码），要挂条码去 ③ 步「条码」栏指认它",
                        digitish[0].Row, c));
            }

            // 列名格本身写的是指令（金沐 D2、邱总 C1 的「一开四」）：它的问题不是"这列不该上纸"，
            // 而是"这列没有名字、名字那句是指令"。已经判成右侧块的列不重复报（一列一条）。
            if (block is null && header.Length > 0
                && InstructionWords.Any(w => header.Contains(w, StringComparison.Ordinal)))
                findings.Add(new HealthFinding(HealthKind.HeaderIsInstruction, HealthLevel.Warning,
                    $"{letter} 列的列名是一句指令（「{Clip(header)}」），不是列名",
                    $"那一列有 {values.Count} 行数据，可它的名字写的是怎么开纸/怎么贴——" +
                    "这既是缺列名（要人去 ② 步命名或让 AI 认），也是一条排版指令（④ 步按它核对开法与张数），两头都要留痕",
                    values.Count > 0 ? values[0].Row : (int?)null, c));

            // K-5：右侧稀疏块（文字模板 / 指令）——判据与门槛都在上面（block）
            if (block is not null)
            {
                // 表内文字模板（金沐/邱总 F 列那种）不写进面板：它是给版式层当参照的，不用用户在这儿拍板。
                // 用户 2026-09-13 原话：「F 列的是模板是给 AI 排版用的所以不用管，不用写」。
                if (block.Kind == SideBlockKind.TextTemplate) continue;

                var already = sideBlocks?.FirstOrDefault(b => b.Column == c);
                if (already is not null)
                {
                    findings.Add(new HealthFinding(HealthKind.SideInstruction, HealthLevel.Notice,
                        $"{letter} 列已按指令改过，不参与映射",
                        $"原样留着 {values.Count} 行（{string.Join(" / ", values.Take(3).Select(v => "「" + Clip(v.Text) + "」"))}）；要放回来点「↩ 撤回这一步」",
                        values[0].Row, c));
                    continue;
                }

                var fill = TryFillFromInstruction(cut, c, values);
                var evidence = "原样：" + string.Join(" / ", values.Take(4).Select(v => "「" + Clip(v.Text) + "」"));
                findings.Add(fill is not null
                    ? new HealthFinding(HealthKind.SideInstruction, HealthLevel.Suggestion,
                        $"{letter} 列数值改成 {HeaderRowDetector.ColumnLetter(fill.SourceColumn)} 列数值",
                        $"{evidence}；指令写的是「{Clip(fill.Reason)}」",
                        values[0].Row, c,
                        new HealthFix(HealthFixKind.AddValueRules, Rules: new[] { fill }))
                    : new HealthFinding(HealthKind.SideInstruction, HealthLevel.Suggestion,
                        $"{letter} 列是指令，不上了纸",
                        $"{evidence}；它决定出纸怎么开、一行出几张，圈出去之后仍要在 ④ 步按它核对",
                        values[0].Row, c,
                        new HealthFix(HealthFixKind.MarkSideColumns, SideBlocks: new[] { block })));
            }
        }
    }

    /// <summary>
    /// 指令原文里点名了「等于哪一列」，才敢把那一列的值填过来（金沐 D3「张数等于件数」→ 找列名含「件数」的那一列）。
    /// <para>两道护栏：① 指令没点名来源列就不做（推不出来只能圈出去）；② 目标列本来装着别的值就不覆盖——
    /// 填 = 抹掉人家写过的东西，那是改数据不是改切法。</para>
    /// </summary>
    private static ValueRule? TryFillFromInstruction(HeaderRowDetector.DetectionResult cut,
        int target, List<(int Row, string Text)> values)
    {
        var text = values.Count > 0 ? values[0].Text : string.Empty;
        if (text.Length == 0) return null;
        var match = EqualsOtherColumn.Match(text);
        if (!match.Success) return null;
        var wanted = HeaderRowDetector.Normalize(match.Groups[1].Value);
        if (wanted.Length == 0) return null;

        var source = -1;
        for (var c = 0; c < cut.Headers.Count; c++)
        {
            if (c == target) continue;
            var header = HeaderRowDetector.Normalize(cut.Headers[c]);
            if (header.Length == 0) continue;
            if (header == wanted || header.Contains(wanted, StringComparison.Ordinal)) { source = c; break; }
        }
        if (source < 0) return null;
        if (values.Any(v => v.Text != text)) return null;   // 目标列已装着别的值 → 不覆盖

        return new ValueRule(ValueRuleKind.FillColumnFrom, target, source, text);
    }

    /// <summary>
    /// 把值改写规则落到行上。<strong>只改软件里的这一份视图</strong>：磁盘上用户的 xlsx 原件一个字不动。
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ApplyValueRules(
        IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<ValueRule> rules)
    {
        if (rules.Count == 0) return rows;
        var result = new List<IReadOnlyList<string>>(rows.Count);
        foreach (var row in rows)
        {
            var line = row.ToArray();
            foreach (var rule in rules)
            {
                if (rule.TargetColumn < 0 || rule.TargetColumn >= line.Length) continue;
                switch (rule.Kind)
                {
                    case ValueRuleKind.FillColumnFrom
                        when rule.SourceColumn >= 0 && rule.SourceColumn < line.Length:
                        line[rule.TargetColumn] = line[rule.SourceColumn] ?? string.Empty;
                        break;
                    case ValueRuleKind.StripStarTail:
                        var cell = line[rule.TargetColumn] ?? string.Empty;
                        if (IsDuplicateStarTail(cell, line, rule.TargetColumn, out var starAt))
                            line[rule.TargetColumn] = cell[..starAt].TrimEnd();
                        break;
                }
            }
            result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// 这一格是不是「* + 重复数字」尾巴：<strong>尾巴是纯数字、且这个数字在同行别处出现过</strong>才算。
    /// <para>扫描与改写共用这一个判据（两处各写一份必走样，§五-122）。品名（<c>OLU4014 *CANDY CLOUDS</c>）
    /// 与规格（<c>35.4*27*30.1</c>、<c>*100ml</c>）都不算 —— 那些是信息，删了就少印东西。</para>
    /// </summary>
    public static bool IsDuplicateStarTail(string text, IReadOnlyList<string> row, int column, out int starAt)
    {
        starAt = -1;
        if (text.Length == 0) return false;
        foreach (Match m in Regex.Matches(text, @"\*\s*(\d{1,4})(?!\d)"))
        {
            var tail = m.Groups[1].Value;
            for (var k = 0; k < row.Count; k++)
            {
                if (k == column) continue;
                if ((row[k] ?? string.Empty).Trim() == tail)
                {
                    starAt = m.Index;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>把一列稀疏内容判成文字模板 / 指令 / 不算（返回 null）。</summary>
    private static SideBlock? ClassifySideBlock(int column, List<(int Row, string Text)> values)
    {
        var instruction = values.Count(v => InstructionWords.Any(w => v.Text.Contains(w, StringComparison.Ordinal)));
        var labelShaped = values.Count(v => LabelShape.IsMatch(v.Text));
        var kind = instruction > values.Count / 2 ? SideBlockKind.Instruction
                 : labelShaped >= Math.Min(2, values.Count) ? SideBlockKind.TextTemplate
                 : (SideBlockKind?)null;
        if (kind is null) return null;
        var sample = string.Join(" / ", values.Take(2).Select(v => Clip(v.Text)));
        return new SideBlock(column, kind.Value, values.Select(v => v.Row).ToList(), sample);
    }

    // ── K-4 / K-8 / K-9 按格看 ───────────────────────────────────────────

    private static void ScanCellShapes(IReadOnlyList<string[]> grid, HeaderRowDetector.DetectionResult cut, List<HealthFinding> findings)
    {
        var starHits = new List<(int Row, int Col, string Text, int StarAt)>();
        var dispImg = new List<(int Row, int Col)>();
        var doubled = new List<(int Row, int Col, string Text)>();

        foreach (var r in cut.DataRowRawIndexes)
        {
            var row = grid[r];
            for (var c = 0; c < row.Length; c++)
            {
                var text = row[c]?.Trim() ?? string.Empty;
                if (text.Length == 0) continue;

                // K-4：`*` 号尾巴。只有「尾巴是纯数字、且这个数字在同行别处出现过」才算重复，可去。
                //      全量扫五家真表：* 出现 405 次 → 303 次是这种重复尾巴，97 次后面跟的是品名
                //      （OLU4014 *CANDY CLOUDS），5 次是规格（35.4*27*30.1、*100ml）。
                //      所以后两类一律不动，只报第一条那种。
                if (IsDuplicateStarTail(text, row, c, out var starAt)) starHits.Add((r, c, text, starAt));

                if (text.StartsWith(DispImgToken, StringComparison.OrdinalIgnoreCase))
                    dispImg.Add((r, c));

                if (TwicePrefixed.IsMatch(text)) doubled.Add((r, c, text));
            }
        }

        if (starHits.Count > 0)
        {
            // 一列一条（用户 2026-09-13：字太长就乱）。文案照他的写法：「将*号及后面删除」。
            foreach (var group in starHits.GroupBy(h => h.Col).OrderBy(g => g.Key))
            {
                var first = group.First();
                findings.Add(new HealthFinding(HealthKind.DuplicateStarTail, HealthLevel.Suggestion,
                    $"{HeaderRowDetector.ColumnLetter(group.Key)} 列 {group.Count()} 处：删掉 * 号及后面",
                    $"例：第 {first.Row + 1} 行「{Clip(first.Text)}」——尾巴那个数同行别处已经写着，重复了。" +
                    "只删这种；* 后是品名的（OLU4014 *CANDY CLOUDS）跟的是规格的（35.4*27*30.1）都不进这条清单",
                    first.Row, group.Key,
                    new HealthFix(HealthFixKind.AddValueRules, Rules: new[]
                    {
                        new ValueRule(ValueRuleKind.StripStarTail, group.Key,
                            Reason: $"{group.Count()} 处 * 号尾巴与同行的数重复"),
                    })));
            }
        }

        if (dispImg.Count > 0)
            findings.Add(new HealthFinding(HealthKind.EmbeddedImagePlaceholder, HealthLevel.Warning,
                $"{dispImg.Count} 格装的是 WPS 嵌入图的占位文本，不是内容",
                $"第 {string.Join("、", dispImg.Take(6).Select(d => (d.Row + 1).ToString(CultureInfo.InvariantCulture)))} 行的 " +
                $"{string.Join("、", dispImg.Select(d => HeaderRowDetector.ColumnLetter(d.Col)).Distinct())} 列原样是 {DispImgToken}…；" +
                "这类图存在另一个部件 xl/cellimages.xml 里，现在读不出来——图会静默少一半，别把这列当文本绑字段"));

        if (doubled.Count > 0)
        {
            var cols = doubled.Select(d => HeaderRowDetector.ColumnLetter(d.Col)).Distinct().ToList();
            var examples = string.Join(" / ", doubled.Take(3).Select(d => $"第 {d.Row + 1} 行「{Clip(d.Text)}」"));
            findings.Add(new HealthFinding(
                HealthKind.MultipleValuesInOneCell, HealthLevel.Warning,
                $"{string.Join("、", cols)} 列有 {doubled.Count} 格一格装了两个值",
                $"例：{examples} —— 这是一个编号范围（如 AJ1 到 AJ3）。若同行的指令写着「一个流水号一张」，" +
                "这一行该出的纸数与现在算出来的不一样，得人工定口径，软件不猜",
                doubled[0].Row, doubled[0].Col));
        }
    }

    /// <summary>GTIN 模 10 校验位（最后一位）。只用于"这列像不像条码"的佐证，不用于拒收。</summary>
    public static bool GtinCheckOk(string digits)
    {
        if (digits.Length < 2 || !digits.All(char.IsDigit)) return false;
        var sum = 0;
        for (var i = 0; i < digits.Length - 1; i++)
        {
            var fromRight = digits.Length - 1 - i;
            sum += (digits[i] - '0') * (fromRight % 2 == 0 ? 1 : 3);
        }
        return (10 - sum % 10) % 10 == digits[^1] - '0';
    }

    private static string Clip(string text)
    {
        var one = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return one.Length <= 28 ? one : one[..28] + "…";
    }
}
