using System.Globalization;
using System.Text.Json;
using LabelGou.Core.Data;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Recognition;

/// <summary>
/// AI 对「这张表该怎么切、这张纸该怎么摆」的一份提案（第 21 棒）。
/// <para><strong>它从哪来</strong>：模型看着整张表的画像（含表头以上的批注、右侧贴的效果图）回的 JSON，
/// 由 <see cref="Parse"/> 清洗成这份结构。<strong>它到哪去</strong>：确认窗上逐条摊给人看，
/// 人点「用这个」之后才变成 <see cref="SheetLayoutChoice"/>（切表指令）与纸规改动。</para>
/// <para><strong>为什么权限这么大还要一层校验</strong>：用户 2026-09-09 的原话是「把权限都给 AI，
/// 手动调整效果很差」。但看错一张表的代价是重印一垛箱子，所以这里守住三条硬线：
/// ① 行号必须落在真实存在的行里，且不许把表头自己剔掉；② 剔完必须还剩至少一行货；
/// ③ 纸规只能从软件里<em>已有</em>的那些里选，纸张尺寸给范围，绝不接受模型随手写的任意毫米数。</para>
/// </summary>
/// <param name="HeaderRow">表头在<b>原表</b>的第几行（1 起，与人看 Excel 的口径一致）；null = 模型没说。</param>
/// <param name="HasHeader">这张表有没有表头行。false = 首行也当数据（就是「少印一张」那个 bug 的解）。</param>
/// <param name="TotalValueRows">要当合计/批注行剔除的<b>原表</b>行号（1 起）。</param>
/// <param name="Layout">行式版式（标签尺寸在它里面，不留第二个真源）。null = 模型没给可出版的行。</param>
/// <param name="SheetSpecName">点名要用的现有纸规名（必须命中清单，见 <see cref="Errors"/>）。</param>
/// <param name="PaperWidthMm">提议的纸张宽（毫米，可空）。只在清单里一张都不合适时才有意义。</param>
/// <param name="PaperHeightMm">提议的纸张高（毫米，可空）。</param>
/// <param name="Columns">每行几枚（0 = 由宽度自动算）。</param>
/// <param name="Rows">每页几行（0 = 由高度自动算）。</param>
/// <param name="FollowsLabel">纸面跟着标签走（一页一枚）。</param>
/// <param name="Warnings">模型自己报的疑点（如「模板顶部写死的 BOLAROM 与这批客户不符」「末行像合计」）。</param>
/// <param name="Reason">模型说的一句人话：为什么这么判。</param>
/// <param name="Notes">软件替它改了什么（夹范围、去重、忽略越界行号）——一条条写清，不许悄悄修好。</param>
/// <param name="Errors">这条提案不能用的原因。非空时确认窗只准看不准「用这个」。</param>
public sealed record AiSheetProposal(
    int? HeaderRow,
    bool? HasHeader,
    IReadOnlyList<int> TotalValueRows,
    RowLayoutSpec? Layout,
    string? SheetSpecName,
    double? PaperWidthMm,
    double? PaperHeightMm,
    int? Columns,
    int? Rows,
    bool? FollowsLabel,
    IReadOnlyList<string> Warnings,
    string? Reason,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Errors)
{
    /// <summary>能用才允许落地（与行式版式那份 <c>RowLayoutProposal.HasSpec</c> 同一条纪律）。</summary>
    public bool IsUsable => Errors.Count == 0;

    /// <summary>是不是什么都没提（模型只回了话没回指令）。什么都没提就别去动用户的表。</summary>
    public bool IsEmpty =>
        HeaderRow is null && HasHeader is null && TotalValueRows.Count == 0 && Layout is null &&
        SheetSpecName is null && PaperWidthMm is null && PaperHeightMm is null &&
        Columns is null && Rows is null && FollowsLabel is null;

    /// <summary>
    /// 折成切表指令。<paramref name="rawRowCount"/> 是原表总行数（用来把 1 起的行号换成 0 起并挡住越界）。
    /// <para>表头那一行永远从剔除名单里去掉：模型确实会一边说「表头在第 1 行」一边把第 1 行列进合计行。</para>
    /// </summary>
    public SheetLayoutChoice ToChoice(int rawRowCount)
    {
        var headerIndex = HasHeader == false ? (int?)null
            : HeaderRow is int hr ? Math.Clamp(hr - 1, 0, Math.Max(0, rawRowCount - 1)) : null;
        var excluded = new List<int>();
        foreach (var row in TotalValueRows)
        {
            if (row < 1 || row > rawRowCount) continue;         // 越界的一律不理（Parse 里已经记过 Note）
            if (headerIndex is int hi && row - 1 == hi) continue;   // 表头自己不能又被剔掉
            if (!excluded.Contains(row - 1)) excluded.Add(row - 1);
        }
        return new SheetLayoutChoice(
            HasHeader == false ? 0 : headerIndex,
            HasHeader ?? true,
            excluded.Count == 0 ? null : excluded);
    }

    /// <summary>
    /// 确认窗上的「会改这几件事」清单（一条一项，人点头前谁也不生效）。
    /// <para><strong>为什么字字都是大白话</strong>（2026-09-09 用户圈图反馈）：「你需要理解使用者不是技术人员，
    /// 他们不知道 rows 什么的需要简洁明了」。所以这里不出现 rows、JSON、字段英文名、
    /// 「数据行」这类口径；提醒与软件改动不挤在这一段里（见 <see cref="Explain"/>），
    /// 否则一屏全是「⚠」，人反而一个都不会去看。</para>
    /// </summary>
    public IReadOnlyList<string> DescribeItems(int rawRowCount)
    {
        var items = new List<string>();
        if (HasHeader == false) items.Add("这张表第一行不是列名，是货 —— 改成它也出一张标签（会多出 1 张）");
        else if (HeaderRow is int hr) items.Add($"列名按你说的算：在原来那张表的第 {hr} 行");
        if (TotalValueRows.Count > 0)
            items.Add("这几行不当货印（它们不是箱子，是合计或备注）：第 " + string.Join("、", TotalValueRows) + " 行");
        if (Layout is { } spec)
        {
            var lines = string.Join("；", spec.Rows.Select(r => r.Content));
            items.Add($"标签上印这几行：{Shrink(lines, 120)}（标签大小 {spec.WidthMm:0.#}×{spec.HeightMm:0.#} 毫米）");
        }
        if (SheetSpecName is not null) items.Add($"一张纸怎么摆：换成「{SheetSpecName}」这一张");
        else if (PaperWidthMm is double pw && PaperHeightMm is double ph)
        {
            var perRow = (Columns ?? 0) <= 0 ? "自己算" : Columns + " 张";
            var perPage = (Rows ?? 0) <= 0 ? "自己算" : Rows + " 行";
            var follow = FollowsLabel == true ? "（纸跟着标签走，一张纸一张标签）" : string.Empty;
            items.Add($"一张纸 {pw / 10:0.#}×{ph / 10:0.#} 厘米，每行摆 {perRow}、每页 {perPage}{follow}");
        }
        if (rawRowCount > 0 && TotalValueRows.Count > 0)
            items.Add($"改完之后会出 {Math.Max(0, rawRowCount - (HasHeader == false ? 0 : 1) - TotalValueRows.Count)} 张标签（以软件重切结果为准）");
        return items;
    }

    /// <summary>
    /// 它自己说的话：提醒（去重、最多 <see cref="MaxExplainLines"/> 条）加一句理由。
    /// <para>与改动清单分开，是为了让人先看清「要改哪几件事」，再看「它担心什么」。</para>
    /// </summary>
    public IReadOnlyList<string> Explain()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<string>();
        foreach (var w in Warnings)
        {
            var key = w.Trim();
            if (key.Length == 0 || !seen.Add(key)) continue;
            unique.Add(Shrink(key, 90));
        }
        var list = unique.Take(MaxExplainLines).ToList();
        if (unique.Count > list.Count) list.Add($"（还有 {unique.Count - list.Count} 条提醒，点「复制全部」能看到）");
        if (!string.IsNullOrWhiteSpace(Reason)) list.Add("它的说法：" + Shrink(Reason.Trim(), 90));
        return list;
    }

    /// <summary>提醒最多列几条（多了等于没有，没人会逐字看）。</summary>
    public const int MaxExplainLines = 5;

    private static string Shrink(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>模型爱用的同义键 → 我们的名字。认不出的宁可当作没提，也不猜一个意思相近的字段塞进去。</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hasHeader"] = "hasHeader", ["hasHeaderRow"] = "hasHeader", ["noHeader"] = "noHeader",
        ["没有表头"] = "noHeader", ["无表头"] = "noHeader",
        ["headerRow"] = "headerRow", ["headerLine"] = "headerRow", ["headerRowIndex"] = "headerRow", ["表头行"] = "headerRow",
        ["totalRows"] = "totalRows", ["totalRow"] = "totalRows", ["summaryRows"] = "totalRows", ["ignoreRows"] = "totalRows",
        ["skipRows"] = "totalRows", ["合计行"] = "totalRows",
        ["sheetSpec"] = "sheetSpec", ["paperSpec"] = "sheetSpec", ["纸规"] = "sheetSpec", ["sheet"] = "sheetSpec",
        ["paperWidthMm"] = "paperWidthMm", ["paperW"] = "paperWidthMm", ["纸张宽"] = "paperWidthMm",
        ["paperHeightMm"] = "paperHeightMm", ["paperH"] = "paperHeightMm", ["纸张高"] = "paperHeightMm",
        ["columns"] = "columns", ["cols"] = "columns", ["每行枚数"] = "columns", ["perRow"] = "columns",
        // 不叫 "rows"：那个键在版式那段里是「哪几行文字」，一个键两个意思以后必错（同 §五-62 那条形状）。
        ["rowsPerPage"] = "paperRows", ["每页行数"] = "paperRows", ["perSheetRows"] = "paperRows", ["paperRows"] = "paperRows",
        ["rows"] = "rows",
        ["followsLabel"] = "followsLabel", ["一页一枚"] = "followsLabel", ["paperFollowsLabel"] = "followsLabel",
        ["warnings"] = "warnings", ["risks"] = "warnings", ["提醒"] = "warnings", ["notes"] = "warnings",
        ["reason"] = "reason", ["why"] = "reason", ["理由"] = "reason",
    };

    /// <summary>
    /// 解析模型原文。<b>从不抛</b>：形状问题一律变成 <see cref="Errors"/> 里的一句人话。
    /// <para>行式版式那一段直接交给 <see cref="RowLayoutJsonParser.Parse"/>——同一份 JSON 两个读者，
    /// 不另写第二套清洗逻辑（两套一定会长得不一样，那是 §五-62 那类「换路复发」的根）。</para>
    /// </summary>
    /// <param name="modelText">模型回的那一段（带围栏或前后解释都收）。</param>
    /// <param name="columns">列画像，透传给版式解析（用来把 <c>{{col:列名}}</c> 对回真表头）。</param>
    /// <param name="rawRowCount">原表总行数（1 起口径的边界，用来挡越界行号）。</param>
    /// <param name="sheetSpecNames">软件里真有的纸规名（不在这份清单里的名字一律拒，见 <see cref="Errors"/>）。</param>
    public static AiSheetProposal Parse(
        string? modelText,
        IReadOnlyList<ColumnPortrait>? columns,
        int rawRowCount,
        IReadOnlyList<string>? sheetSpecNames = null)
    {
        var notes = new List<string>();
        var errors = new List<string>();

        var json = ExtractJsonObject(modelText);
        if (json is null)
            return Bad("它这次回的话里没有可执行的方案，什么都没改。你可以再点一次，或把图附上让它重看。");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            return Bad($"它回的那段方案没读完（格式不对），什么都没改：{ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Bad("它回的内容不是可执行的方案，什么都没改。");

            var fields = Normalize(doc.RootElement);

            // ── 表头 ──
            bool? hasHeader = BoolField(fields, "hasHeader");
            if (BoolField(fields, "noHeader") == true) hasHeader = false;
            var headerRow = IntField(fields, "headerRow");
            if (headerRow is int hr)
            {
                // 模型常给 0 起的下标。0 不可能是合法的 Excel 行号（那等于第 0 行），
                // 而它又确实想指第一行，所以 0 折算成 1 并说明——这比拒掉一条提案有用，也不静默。
                if (hr == 0) { headerRow = 1; notes.Add("它把行号写成 0 了，按第一行算（表里没有第 0 行）"); }
                if (hr > rawRowCount)
                {
                    notes.Add($"它说列名在表里第 {hr} 行，可这张表一共只有 {rawRowCount} 行 —— 这条没采纳");
                    headerRow = null;
                }
            }

            // ── 合计/批注行 ──
            var totalRows = new List<int>();
            foreach (var v in IntListField(fields, "totalRows"))
            {
                if (v <= 0) { notes.Add($"它给了一个不存在的行号（{v}），这条没采纳"); continue; }
                if (v > rawRowCount) { notes.Add($"它要去掉表里第 {v} 行，可这张表一共只有 {rawRowCount} 行 —— 这条没采纳"); continue; }
                if (!totalRows.Contains(v)) totalRows.Add(v);
            }
            if (headerRow is int h && totalRows.Contains(h))
            {
                totalRows.Remove(h);
                notes.Add($"第 {h} 行是列名那一行，不能又当合计行去掉 —— 这条没采纳");
            }

            // ── 版式（含标签尺寸）：交给那一份已有的解析 ──
            // 只有它真给了 rows 那段才拿版式的错去整份拒——提示词明说「没参照时把版式那段省略」，
            // 省略不是错，拿它当错等于把「只报事实」这份提案也拒了。
            var hasRows = fields.ContainsKey("rows");
            var layout = hasRows ? RowLayoutJsonParser.Parse(modelText, columns) : null;
            if (layout is not null)
            {
                foreach (var n in layout.Notes) notes.Add("标签内容：" + n);
                foreach (var e in layout.Errors) errors.Add("标签内容：" + e);
            }
            else
            {
                notes.Add("这次没重排你的标签内容（它没说标签上该印哪几行），模板还是你现在用的那张");
            }

            // ── 纸规：只能选真有的，或给一张合法的新纸 ──
            var specName = TextField(fields, "sheetSpec", 60);
            if (specName is not null && sheetSpecNames is { Count: > 0 })
            {
                var hit = MatchSpec(specName, sheetSpecNames);
                if (hit is null)
                    errors.Add($"你说的那张纸「{specName}」这台机器上没有，不能凭空造一张。" +
                               $"现在能选的：{string.Join("、", sheetSpecNames.Take(8))}");
                else if (!string.Equals(hit, specName, StringComparison.Ordinal))
                {
                    notes.Add($"它写的纸规名字有点差，按你机器上那张「{hit}」算");
                    specName = hit;
                }
            }

            var paperW = DoubleField(fields, "paperWidthMm");
            var paperH = DoubleField(fields, "paperHeightMm");
            foreach (var (val, name) in new[] { (paperW, "纸宽"), (paperH, "纸高") })
            {
                if (val is null) continue;
                if (val is < 30 or > 2000)
                    notes.Add($"{name} {val:0.#} 毫米不在合理范围（30~2000），这条没采纳");
            }
            if (paperW is < 30 or > 2000 || paperH is < 30 or > 2000) { paperW = null; paperH = null; }

            var cols = Clamp(IntField(fields, "columns"), 0, 40, "每行枚数", notes);
            var rowsPerPage = Clamp(IntField(fields, "paperRows"), 0, 40, "每页行数", notes);

            return new AiSheetProposal(
                headerRow, hasHeader, totalRows, layout?.Spec, specName,
                paperW, paperH, cols, rowsPerPage, BoolField(fields, "followsLabel"),
                StringListField(fields, "warnings"), TextField(fields, "reason", 400),
                notes, errors);
        }

        AiSheetProposal Bad(string why) => new(null, null, Array.Empty<int>(), null, null,
            null, null, null, null, null, Array.Empty<string>(), null, Array.Empty<string>(), new[] { why });
    }

    private static int? Clamp(int? value, int min, int max, string what, List<string> notes)
    {
        if (value is null) return null;
        if (value < min || value > max)
        {
            notes.Add($"{what} {value} 不在合理范围（{min}~{max}），这条没采纳");
            return null;
        }
        return value;
    }

    /// <summary>纸规名的宽松匹配：去空格后精确 → 互相包含。认不出就返回 null（拒，不猜）。</summary>
    private static string? MatchSpec(string wanted, IReadOnlyList<string> names)
    {
        var w = wanted.Replace(" ", string.Empty);
        foreach (var n in names)
            if (string.Equals(n.Replace(" ", string.Empty), w, StringComparison.OrdinalIgnoreCase)) return n;
        foreach (var n in names)
            if (n.Contains(w, StringComparison.OrdinalIgnoreCase) || w.Contains(n, StringComparison.OrdinalIgnoreCase)) return n;
        return null;
    }

    private static string? ExtractJsonObject(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        if (s.Length == 0) return null;
        var fence = s.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var start = s.IndexOf('{', fence);
            var end = s.LastIndexOf('}');
            if (start >= 0 && end > start) return s[start..(end + 1)];
        }
        var first = s.IndexOf('{');
        var last = s.LastIndexOf('}');
        return first >= 0 && last > first ? s[first..(last + 1)] : null;
    }

    private static Dictionary<string, JsonElement> Normalize(JsonElement root)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in root.EnumerateObject())
            if (Aliases.TryGetValue(prop.Name, out var key)) map[key] = prop.Value;
        return map;
    }

    private static bool? BoolField(Dictionary<string, JsonElement> f, string key)
    {
        if (!f.TryGetValue(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b
                : el.GetString()?.Trim() is "1" or "yes" or "true" ? true
                : el.GetString()?.Trim() is "0" or "no" or "false" ? false : (bool?)null,
            JsonValueKind.Number => el.TryGetInt32(out var n) ? n != 0 : null,
            _ => null,
        };
    }

    private static int? IntField(Dictionary<string, JsonElement> f, string key)
    {
        if (!f.TryGetValue(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        var text = ReadText(el);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? DoubleField(Dictionary<string, JsonElement> f, string key)
    {
        if (!f.TryGetValue(key, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d)) return d;
        var text = ReadText(el);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? ReadText(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => el.GetRawText(),
        _ => null,
    };

    private static IReadOnlyList<int> IntListField(Dictionary<string, JsonElement> f, string key)
    {
        var list = new List<int>();
        if (!f.TryGetValue(key, out var el)) return list;
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (int.TryParse(ReadText(item), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) list.Add(v);
            return list;
        }
        // 也收 "412,413" 这种把数组写成一句话的（模型真的会这么干）
        foreach (var part in (ReadText(el) ?? string.Empty).Split(new[] { ',', '，', '、', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) list.Add(v);
        return list;
    }

    private static IReadOnlyList<string> StringListField(Dictionary<string, JsonElement> f, string key)
    {
        var list = new List<string>();
        if (!f.TryGetValue(key, out var el)) return list;
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var text = ReadText(item)?.Trim();
                if (!string.IsNullOrEmpty(text) && text.Length > 1) list.Add(text.Length > 200 ? text[..200] + "…" : text);
            }
            return list;
        }
        var one = ReadText(el)?.Trim();
        if (!string.IsNullOrEmpty(one)) list.Add(one.Length > 200 ? one[..200] + "…" : one);
        return list;
    }

    private static string? TextField(Dictionary<string, JsonElement> f, string key, int maxChars)
    {
        if (!f.TryGetValue(key, out var el)) return null;
        var text = ReadText(el)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > maxChars ? text[..maxChars] + "…" : text;
    }
}
