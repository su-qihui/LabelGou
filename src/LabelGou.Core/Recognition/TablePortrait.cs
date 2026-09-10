using LabelGou.Core.Data;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 一列在「表格画像」里的样子。
/// </summary>
/// <param name="Index">列号（0 起）。</param>
/// <param name="Header">表头原样。渲染端 <c>{{col:列名}}</c> 与映射方案都按它<strong>精确</strong>匹配，所以不许折平。</param>
/// <param name="Label">给人和模型看的那一行（表头带换行时折成「 / 」）。</param>
/// <param name="BoundField">这一列连到的字段键；一个字段都没连上它是 null。</param>
/// <param name="BoundFieldName">连到的字段中文名；没连是 null。</param>
/// <param name="Samples">前几个非空样例（已折平并截断）。</param>
/// <param name="NonEmptyCount">整列有多少行填了东西（让模型能分清"这列是数据"还是"只有表尾一句批注"）。</param>
public sealed record ColumnPortrait(
    int Index,
    string Header,
    string Label,
    string? BoundField,
    string? BoundFieldName,
    IReadOnlyList<string> Samples,
    int NonEmptyCount);

/// <summary>
/// 「这张表到底有什么」的机器画像——给 AI 看的第一份材料。
/// <para><strong>为什么要有它</strong>（M7 第 15 棒，用户 2026-09-08 点出的根因）：
/// 以前 AI 只拿到「已经连上字段的那几列」（<c>MainWindow.WireAi</c> 里一句
/// <c>FieldRows.Where(r => r.Mapped)</c>），所以 ① 自动绑定猜错了它无从知道、也没法纠正，
/// ② 表里那些「没连上字段的列」（流水号、每箱品名、厂商手写的成品文字块）对它根本不存在，
/// ③ 一张全没连上的表（TOP 那种 0 字段）直接把它挡在门外。用户的话是：
/// 「即使我把正确的排版给它，它也按表格的来」——更准确说是「按连上的那三五个字段来」。</para>
/// <para><strong>它只做一件事</strong>：把整张表如实摊开（含没连上的列、每列的真实样例、填了几行），
/// 判断哪些要哪些不要留给模型，最后由人点头。<strong>不在此处清洗、不在此处裁决</strong>。</para>
/// </summary>
public static class TablePortrait
{
    /// <summary>每列默认摊几个样例值。
    /// <para>第 28 棒从 3 提到 6：邱总表 F 列那块「从标签抄下来」的模板正好四行，
    /// 摊 3 个把最后一行（QTY: 96 PCS）截掉——模型如实说「看不全」就不敢给 rows，
    /// 表里的模板于是白摆在提示词里；四到六行是唛头模板的常见长度，按这个留。</para></summary>
    public const int DefaultSamples = 6;

    /// <summary>单个样例最多多少字（唛头样例值常常整段文字，不给上限会挤爆提示词）。</summary>
    public const int MaxSampleChars = 40;

    /// <summary>提示词里最多摊多少列（超了就说明这张表本身有问题，先让人看）。</summary>
    public const int MaxColumnsInPrompt = 30;

    /// <summary>前导批注最多摊几行（再多就不是批注而是另一张表了）。</summary>
    public const int MaxPreambleRowsInPrompt = 12;

    /// <summary>
    /// 逐列造画像。<paramref name="profile"/> 是「自动绑定目前连成什么样」，
    /// 连上与否都<strong>如实标出来</strong>——模型需要知道我们的猜测，才谈得上纠正它。
    /// </summary>
    public static IReadOnlyList<ColumnPortrait> Build(
        TabularData data, MappingProfile? profile, int sampleCount = DefaultSamples)
    {
        if (data is null) return Array.Empty<ColumnPortrait>();
        sampleCount = Math.Clamp(sampleCount, 0, 10);

        var list = new List<ColumnPortrait>(data.ColumnCount);
        for (var c = 0; c < data.ColumnCount; c++)
        {
            var header = data.Headers[c] ?? string.Empty;
            var bound = profile?.Mappings.FirstOrDefault(m => m.IsBound && m.ColumnIndex == c);
            string? boundName = null;
            if (bound is not null && MarkFieldCatalog.TryGet(bound.Field, out var def))
                boundName = def.ChineseName;

            var samples = new List<string>(sampleCount);
            var nonEmpty = 0;
            for (var r = 0; r < data.RowCount; r++)
            {
                var cell = data.GetCell(r, c);
                if (string.IsNullOrWhiteSpace(cell)) continue;
                nonEmpty++;
                if (samples.Count < sampleCount)
                {
                    var shown = Shrink(ColumnLabel.SingleLine(cell));
                    if (!samples.Contains(shown, StringComparer.Ordinal)) samples.Add(shown);
                }
            }

            list.Add(new ColumnPortrait(
                Index: c,
                Header: header,
                Label: ColumnLabel.SingleLine(header),
                BoundField: bound?.Field.ToString(),
                BoundFieldName: boundName,
                Samples: samples,
                NonEmptyCount: nonEmpty));
        }
        return list;
    }

    /// <summary>
    /// 摊成给模型看的文字：每列一行，写清连上了没有、样例长什么样、整列填了几行。
    /// <para>「没连到任何字段」这一句是关键——以前模型看不见这些列，现在不仅看得见，
    /// 还知道我们没连上它，于是它可以用 <c>{{col:列名}}</c> 直取。</para>
    /// </summary>
    public static string Describe(IReadOnlyList<ColumnPortrait> columns, int rowCount)
        => Describe(columns, rowCount, null, null);

    /// <summary>
    /// 同上，但把【表头以上的批注行】与【贴着的图】也摊进去（第 20 棒）。
    /// <para>为什么这两块必须给模型：用户 2026-09-09 定的主路径是「AI 自己看表」，
    /// 而纸规、总件数、客户名常写在表头以上的行里，标签该长什么样常以贴图形式挂在右侧。
    /// 不给这两样，模型就只能凭列名猜——猜错就是印错货。</para>
    /// <para>没图也要写明「没图」：这是“不许造模板”那道闸门的判据（§十-A-27 新增）。</para>
    /// </summary>
    public static string Describe(
        IReadOnlyList<ColumnPortrait> columns,
        int rowCount,
        IReadOnlyList<IReadOnlyList<string>>? preamble,
        IReadOnlyList<SheetImage>? images)
    {
        if (columns is null || columns.Count == 0) return "（这张表一列都没读出来）";
        var sb = new System.Text.StringBuilder();
        sb.Append("整张表 ").Append(rowCount).Append(" 行 × ").Append(columns.Count).Append(" 列");
        if (columns.Count > MaxColumnsInPrompt)
            sb.Append("（下面只列前 ").Append(MaxColumnsInPrompt).Append(" 列）");
        sb.Append("：\n");
        foreach (var col in columns.Take(MaxColumnsInPrompt))
        {
            sb.Append("  - 第").Append(col.Index + 1).Append("列「")
              .Append(col.Label.Length == 0 ? "（表头是空的）" : col.Label).Append('」');
            sb.Append(col.BoundField is null ? "：没连到任何字段（软件现在不用它）"
                                             : $"：连到字段 {col.BoundField}（{col.BoundFieldName}）");
            sb.Append("；整列填了 ").Append(col.NonEmptyCount).Append(" 行");
            sb.Append(col.Samples.Count == 0 ? "；一个样例值都没有"
                                             : "；样例 " + string.Join(" ｜ ", col.Samples));
            sb.Append('\n');
        }

        if (preamble is { Count: > 0 })
        {
            sb.Append("表头以上还有 ").Append(preamble.Count).Append(" 行（软件没拿它当数据，但批注常写在这里，原文照录）：\n");
            foreach (var (row, i) in preamble.Take(MaxPreambleRowsInPrompt).Select((r, i) => (r, i)))
            {
                var cells = row
                    .Select((cell, c) => (cell, c))
                    .Where(t => !string.IsNullOrWhiteSpace(t.cell))
                    .Select(t => HeaderRowDetector.ColumnLetter(t.c) + "「" + Shrink(ColumnLabel.SingleLine(t.cell)) + "」");
                if (!cells.Any()) continue;
                sb.Append("  - 原表第 ").Append(i + 1).Append(" 行：").Append(string.Join(" ｜ ", cells)).Append('\n');
            }
        }

        if (images is { Count: > 0 })
        {
            sb.Append("这张表里还贴了 ").Append(images.Count).Append(" 张图（可能就是模板或效果照片，已随本条消息发给你看）：\n");
            foreach (var img in images.Take(6))
                sb.Append("  - ").Append(img.Describe()).Append('\n');
        }
        else
        {
            sb.Append("这张表里没有贴任何效果图或模板截图。\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// 把模型写的 <c>col:</c> 后面的列名对上真表头：先按原样精确，再按去首尾空白，最后按折平显示名。
    /// <para>返回<strong>原样表头</strong>（渲染端查 <c>col:</c> 键要用它），认不出返回 null——
    /// 认不出就丢掉并写进 Notes，绝不放行一个不存在的列名（那等于让模型编造数据）。</para>
    /// </summary>
    public static string? ResolveHeader(IReadOnlyList<ColumnPortrait>? columns, string? token)
    {
        if (columns is null || columns.Count == 0 || string.IsNullOrWhiteSpace(token)) return null;
        var wanted = token.Trim();
        foreach (var col in columns)
            if (string.Equals(col.Header, wanted, StringComparison.Ordinal)) return col.Header;
        foreach (var col in columns)
            if (string.Equals(col.Header.Trim(), wanted, StringComparison.Ordinal)) return col.Header;
        foreach (var col in columns)
            if (string.Equals(col.Label, wanted, StringComparison.Ordinal)) return col.Header;
        foreach (var col in columns)
            if (string.Equals(col.Label, wanted, StringComparison.OrdinalIgnoreCase)) return col.Header;
        return null;
    }

    private static string Shrink(string text)
        => text.Length <= MaxSampleChars ? text : text[..MaxSampleChars] + "…";
}
