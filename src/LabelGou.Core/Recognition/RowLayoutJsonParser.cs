using System.Text.Json;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 一次「AI 出排版」的解析结果。<see cref="Spec"/> 为 null 或 <see cref="Errors"/> 非空时**不许**落库。
/// <para><see cref="Notes"/> 是"我替你改了什么"的清单：模型给的东西总是这里越界那里拼错，
/// 悄悄修好再给用户看等于骗他，所以每一条修正都要写出来摊在确认界面上。</para>
/// </summary>
public sealed record RowLayoutProposal(
    RowLayoutSpec? Spec,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Errors)
{
    public bool HasSpec => Spec is not null && Errors.Count == 0;
}

/// <summary>
/// 把模型回的**原文**洗成一份受约束的 <see cref="RowLayoutSpec"/>（M7 第 11 棒，「AI 排版」的入口）。
/// <para>为什么要有这一层而不是直接 <c>JsonSerializer.Deserialize&lt;RowLayoutSpec&gt;</c>：
/// 模型回的东西实测有三种脏法——① 整段被 <c>```json</c> 围栏或前后解释文字裹着；
/// ② 数字写成字符串（<c>"widthMm": "140"</c>）；③ 键名同义（<c>text</c> 当 <c>content</c>、<c>size</c> 当 <c>sizePt</c>）。
/// 直接反序列化要么抛要么静默丢，用户看到的就是一句"模型没给出可用版式"，跟没做一样。</para>
/// <para><strong>这里只做清洗与夹范围，不做几何</strong>：毫米坐标的最终裁决仍是
/// <see cref="RowLayoutSpec.Build"/>（排不出返 null）+ <see cref="TemplateValidator"/>（有 Error 拒入库），
/// 也就是「AI 出方案、引擎保精度」这条不变（§五-10）。本类不引用 UI，纯函数可单测。</para>
/// </summary>
public static class RowLayoutJsonParser
{
    /// <summary>行数上限。真样张最多见过 5 行（TOP 那种带备注行的），再多就是模型在编。</summary>
    public const int MaxRows = 6;

    /// <summary>单行内容的长度上限：超了基本是模型把整段说明塞进一行里了。</summary>
    public const int MaxContentChars = 120;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>模型爱用的同义键名 → 我们的属性名。只认这几个，认不出来的宁可报"没有这一行"。</summary>
    private static readonly Dictionary<string, string> RowAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["content"] = "content", ["text"] = "content", ["内容"] = "content",
        ["weight"] = "weight", ["权重"] = "weight", ["比例"] = "weight",
        ["align"] = "align", ["alignment"] = "align", ["对齐"] = "align",
        ["sizept"] = "sizept", ["size"] = "sizept", ["fontsize"] = "sizept", ["字号"] = "sizept",
        ["stretch"] = "stretch", ["撑满"] = "stretch", ["big"] = "stretch",
        ["bold"] = "bold", ["加粗"] = "bold",
    };

    private static readonly Dictionary<string, string> SpecAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rows"] = "rows", ["行"] = "rows", ["lines"] = "rows",
        ["widthmm"] = "widthmm", ["width"] = "widthmm", ["宽"] = "widthmm",
        ["heightmm"] = "heightmm", ["height"] = "heightmm", ["高"] = "heightmm",
        ["paddingmm"] = "paddingmm", ["padding"] = "paddingmm", ["留白"] = "paddingmm",
        ["gapmm"] = "gapmm", ["gap"] = "gapmm", ["行距"] = "gapmm",
        ["drawborder"] = "drawborder", ["border"] = "drawborder", ["边框"] = "drawborder",
        ["name"] = "name", ["名称"] = "name",
        ["note"] = "note", ["说明"] = "note",
    };

    /// <summary>
    /// 解析模型原文。<b>从不抛</b>：任何形状问题都变成 <see cref="RowLayoutProposal.Errors"/> 里的一句人话。
    /// </summary>
    public static RowLayoutProposal Parse(string? modelText)
    {
        var notes = new List<string>();
        var errors = new List<string>();

        var json = ExtractJsonObject(modelText);
        if (json is null)
            return new RowLayoutProposal(null, notes, new[] { "模型的回答里没有 JSON 对象，没法当排版方案用。" });

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
            return new RowLayoutProposal(null, notes, new[] { $"那段 JSON 读不开：{ex.Message}" });
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new RowLayoutProposal(null, notes, new[] { "JSON 的根不是对象，没法当排版方案用。" });

            var spec = ReadSpec(doc.RootElement, notes, errors);
            return errors.Count > 0
                ? new RowLayoutProposal(null, notes, errors)
                : new RowLayoutProposal(spec, notes, errors);
        }
    }

    private static RowLayoutSpec? ReadSpec(JsonElement root, List<string> notes, List<string> errors)
    {
        var fields = Normalize(root, SpecAliases);

        var spec = new RowLayoutSpec
        {
            // Id 由骨架自己生成（user.rows-xxxxxxxx）：模型给的 id 一律不信，否则它会造出跟模板库撞号的东西。
            Name = ClampText(TextField(fields, "name"), 40) ?? "AI 建议版式",
            Note = ClampText(TextField(fields, "note"), 200) ?? "AI 出的行式方案，未经核对不入库。",
        };

        spec.WidthMm = Clamp(TextField(fields, "widthmm"), 140, TemplateValidator.MinLabelSideMm, TemplateValidator.MaxLabelSideMm, "标签宽度", notes);
        spec.HeightMm = Clamp(TextField(fields, "heightmm"), 100, TemplateValidator.MinLabelSideMm, TemplateValidator.MaxLabelSideMm, "标签高度", notes);
        spec.PaddingMm = Clamp(TextField(fields, "paddingmm"), 5, 0, Math.Min(spec.WidthMm, spec.HeightMm) / 3, "四周留白", notes);
        spec.GapMm = Clamp(TextField(fields, "gapmm"), 2, 0, 10, "行间距", notes);
        spec.DrawBorder = BoolField(fields, "drawborder");

        if (!fields.TryGetValue("rows", out var rowsEl) || rowsEl.ValueKind != JsonValueKind.Array)
        {
            errors.Add("JSON 里没有 rows（行清单），排不出任何一行文字。");
            return null;
        }

        foreach (var item in rowsEl.EnumerateArray())
        {
            if (spec.Rows.Count >= MaxRows)
            {
                notes.Add($"模型给了 {rowsEl.GetArrayLength()} 行，超过 {MaxRows} 行的部分已丢掉（唛头上不会再有那么多行）。");
                break;
            }
            if (item.ValueKind != JsonValueKind.Object)
            {
                notes.Add("rows 里有一项不是对象，已跳过。");
                continue;
            }

            var cells = Normalize(item, RowAliases);
            var raw = TextField(cells, "content");
            var content = SanitizeTokens(raw, notes);
            if (string.IsNullOrWhiteSpace(content))
            {
                notes.Add("有一行去掉不认识的东西后空了，已丢掉这一行。");
                continue;
            }

            var row = new RowSpec
            {
                Content = content,
                Weight = ClampWeight(TextField(cells, "weight")),
                Align = ParseAlign(TextField(cells, "align")),
                SizePt = Clamp(TextField(cells, "sizept"), 12, TemplateValidator.MinFontPt, TemplateValidator.MaxFontPt, "行字号", notes),
                Stretch = BoolField(cells, "stretch"),
                Bold = cells.TryGetValue("bold", out var bold) ? BoolField(cells, "bold") : true,
                // 字体不让模型挑：它报的字体名这台机器多半没有，掉字比难看更糟（§五-6）。
                FontFamily = TemplateElement.DefaultFont,
            };
            spec.Rows.Add(row);
        }

        if (spec.Rows.Count == 0)
        {
            errors.Add("清洗完没有任何一行可印，这份方案不能用来生成模板。");
            return null;
        }
        return spec;
    }

    /// <summary>
    /// 只留白名单字段与固定文字：模型造的 <c>{{Foo}}</c> 会被 <see cref="TemplateValidator"/> 判 Error 拒入库，
    /// 与其让用户对着一条看不懂的红字，不如在这里剥掉并写明剥了谁。
    /// </summary>
    private static string SanitizeTokens(string? raw, List<string> notes)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length > MaxContentChars)
        {
            notes.Add($"有一行 {text.Length} 字太长了（唛头一行放不下），截到 {MaxContentChars} 字。");
            text = text[..MaxContentChars];
        }

        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && i + 1 < text.Length && text[i + 1] == '{')
            {
                var end = text.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    sb.Append(text[i]);
                    continue;
                }
                var token = text[(i + 2)..end].Trim();
                if (MarkFieldCatalog.IsKnownKey(token) || TemplateTokenizer.IsBuiltInToken(token)
                    || token.StartsWith("col:", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append("{{").Append(token).Append("}}");
                }
                else
                {
                    notes.Add($"字段「{token}」不在字段清单里，已从这一行去掉（其余文字保留）。");
                }
                i = end + 1;
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>把 JSON 对象按别名表收进一张字典；认不出的键忽略（模型爱加没用的键）。</summary>
    private static Dictionary<string, JsonElement> Normalize(JsonElement obj, Dictionary<string, string> aliases)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.EnumerateObject())
        {
            var key = prop.Name.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
            if (aliases.TryGetValue(key, out var target) && !map.ContainsKey(target))
                map[target] = prop.Value;
        }
        return map;
    }

    private static string? TextField(Dictionary<string, JsonElement> map, string key)
    {
        if (!map.TryGetValue(key, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool BoolField(Dictionary<string, JsonElement> map, string key)
    {
        var text = TextField(map, key)?.Trim();
        return text is not null && (text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text == "1" || text.Equals("yes", StringComparison.OrdinalIgnoreCase) || text == "是" || text == "有");
    }

    private static HorizontalAlign ParseAlign(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "center" or "middle" or "居中" or "中" => HorizontalAlign.Center,
        "right" or "far" or "右" => HorizontalAlign.Right,
        _ => HorizontalAlign.Left,
    };

    private static double ClampWeight(string? text)
    {
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var w)) return 1;
        var clamped = Math.Clamp(w, 0.05, 8);
        return clamped == w ? w : clamped;
    }

    /// <summary>
    /// 夹范围。<b>没给</b>与<b>给了但不合法</b>是两回事：前者默默用默认值（模型没提就是没意见，
    /// 每条都记一笔会把确认界面刷屏）；后者与夹过范围的一律写进 notes，不许悄悄改用户看得见的数值。
    /// </summary>
    private static double Clamp(string? text, double fallback, double min, double max, string what, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            notes.Add($"{what}填的是「{text}」不是数字，按 {fallback} 处理。");
            return fallback;
        }
        var clamped = Math.Clamp(value, min, max);
        if (Math.Abs(clamped - value) > 1e-9)
            notes.Add($"{what} {value:0.##} 不在 {min:0.##}~{max:0.##} 可印范围内，已夹到 {clamped:0.##}。");
        return clamped;
    }

    private static string? ClampText(string? text, int maxChars)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > maxChars ? trimmed[..maxChars] : trimmed;
    }

    /// <summary>
    /// 从原文里抠出第一个花括号配平的对象：跳过字符串字面量里的花括号与转义引号。
    /// <para>为什么不用正则：嵌套对象会把非贪婪正则截断在第一个 <c>}}</c> 上，而围栏与前后解释文字
    /// 让「第一个 { 到最后一个 }」这种粗暴写法也不可靠。</para>
    /// </summary>
    private static string? ExtractJsonObject(string? text)
    {
        var s = text ?? string.Empty;
        var start = s.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return s[start..(i + 1)];
                    break;
            }
        }
        return null;        // 花括号没配平 = 模型把话说到一半断了
    }
}

/// <summary>
/// 「让 AI 出一版排版」的提示词（M7 第 11 棒）。
/// <para>字段清单从 <see cref="MarkFieldCatalog.Mappable"/> 现生成，不手写第二份：
/// 目录改了提示词不能悄悄落后（同 <c>AiChatWindow.SystemTurn</c> 的理由）。</para>
/// </summary>
public static class RowLayoutPrompt
{
    /// <summary>
    /// <paramref name="fields"/> 是「这个客户这张表里真有的列」：键、中文名、一个样例值。
    /// 只给目录全量清单会让模型去用表里根本没有的字段，印出来就是空格子。
    /// </summary>
    public static string Build(
        IReadOnlyList<(string Key, string Name, string Sample)> fields,
        double widthMm, double heightMm, string? userNote)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("你是外贸纸箱唛头排版助手。请给这张标签出行式版式：唛头 = 自上而下若干行文字，")
          .Append("每行可以是固定文字与 {{字段键}} 混排（例如 \"QTY：{{Quantity}} pcs\"）。\n")
          .Append($"标签尺寸 {widthMm:0.#} × {heightMm:0.#} 毫米。\n")
          .Append("这张表里真实可用的字段只有下面这些（键：中文名，样例值）：\n");
        foreach (var f in fields)
            sb.Append("  - ").Append(f.Key).Append("：").Append(f.Name)
              .Append("，样例 ").Append(string.IsNullOrWhiteSpace(f.Sample) ? "（空）" : f.Sample).Append('\n');
        sb.Append("\n只回一个 JSON 对象，不要任何解释文字、不要 Markdown 围栏。形状：\n")
          .Append("{\"name\":\"...\",\"widthMm\":").Append(widthMm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
          .Append(",\"heightMm\":").Append(heightMm.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
          .Append(",\"paddingMm\":5,\"gapMm\":2,\"drawBorder\":false,")
          .Append("\"rows\":[{\"content\":\"BOLAROM\",\"weight\":1.6,\"align\":\"center\",\"stretch\":true,\"bold\":true},")
          .Append("{\"content\":\"Item no：{{ItemNo}}\",\"weight\":1,\"align\":\"left\",\"sizePt\":14,\"stretch\":false}]}\n")
          .Append("硬要求：rows 最多 6 行；content 里只能用上面列出的字段键，别的字段一律不要写；")
          .Append("大字行用 stretch:true（字号由行高反算，别自己填字号）；明细行 stretch:false 并给 sizePt（3~130）；")
          .Append("不要输出任何毫米坐标、元素位置、字体名——那些由软件算，不由你算。\n");
        if (!string.IsNullOrWhiteSpace(userNote))
            sb.Append("用户补充：").Append(userNote.Trim()).Append('\n');
        sb.Append("用中文写 name 与 note。");
        return sb.ToString();
    }
}
