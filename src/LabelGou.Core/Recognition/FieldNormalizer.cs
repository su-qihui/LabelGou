using System.Globalization;
using System.Text.RegularExpressions;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>规范化后的值 + 一条人话告警（null 表示没问题）。</summary>
public sealed record NormalizedField(string Value, string? Warning)
{
    public bool HasWarning => Warning is not null;

    public override string ToString() => Warning is null ? Value : $"{Value}（{Warning}）";
}

/// <summary>
/// 把识别到的原始文本收敛成「能直接印到唛头上」的写法（定案 D14 的后半段）。
/// <para>三条硬规矩：</para>
/// <list type="number">
/// <item>单位与量纲必须校验——重量不能为负、体积不能为零、尺寸必须是三段（§五-9 的数字防线）。</item>
/// <item>认不出来的<strong>保留原文并告警</strong>，绝不硬凑一个「看起来对」的值。宁可让人看一眼，
/// 不能让工具替工厂编一个数。</item>
/// <item><see cref="CompareForm"/> 只用于两通道比对，不参与印面文本，避免比对规则改动就改变印刷结果。</item>
/// </list>
/// </summary>
public static class FieldNormalizer
{
    /// <summary>印面上可接受的文本长度上限，超了判为「大概把两行粘成一行」。</summary>
    public const int MaxTextLength = 120;

    private static readonly Regex SizePattern = new(
        @"(\d{1,4}(?:\.\d+)?)\s*[x]\s*(\d{1,4}(?:\.\d+)?)\s*[x]\s*(\d{1,4}(?:\.\d+)?)\s*(cm|mm|m)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex WeightUnit = new(
        @"(?i)\b(kgs?|kilos?|kilograms?|lbs?|pounds?)\b|公斤|千克|磅",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CartonSplitPattern = new(
        @"^\s*(?:no\.?|ctn\.?|box)?\s*(\d+)\s*[/／\-]\s*(\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 能直接定下来的日期形：只有「年在前」与 ISO 两种。
    /// <para>刻意不包含 <c>dd/MM/yyyy</c> 与 <c>dd-MM-yyyy</c>：<c>03/04/2026</c> 会静默按欧式读成 4 月 3 日，
    /// 而不补零的 <c>3/4/2026</c> 却走下面的歧义启发式带告警 —— 同一个日期仅因补零就换月且不吭声
    /// （批次一-6）。把它们交给启发式，补零与否就能拿到同一个答案与同一句「请核对」。</para>
    /// </summary>
    private static readonly string[] ExactDateFormats =
    {
        "yyyy-MM-dd", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd",
    };

    /// <summary>规范化一个字段值。空值原样返回空，交由上层决定「整条隐藏」还是「待补」。</summary>
    public static NormalizedField Normalize(MarkFieldKey field, string? raw)
    {
        // 先修小数点再清洗：本机实测 OCR 把 G.W. 读成 "25 ． 5 KGS"，
        // 不修的话重量正则会在 "5 KGS" 处匹配成功，把 25.5 静默印成 5。
        var text = TextNormalizer.Squeeze(TextNormalizer.RepairDecimalSeparators(raw));
        if (text.Length == 0) return new NormalizedField(string.Empty, null);

        var def = MarkFieldCatalog.Get(field);
        return def.Kind switch
        {
            MarkValueKind.Image => new NormalizedField(text, null),
            MarkValueKind.Date => NormalizeDate(text),
            MarkValueKind.Weight => NormalizeWeight(field, text),
            MarkValueKind.Volume => NormalizeVolume(field, text),
            MarkValueKind.Integer => NormalizeInteger(field, text),
            _ => NormalizeText(field, text),
        };
    }

    /// <summary>比对形：两通道是否「同一个值」只看这个，忽略大小写、标点、空格与单位写法差异。</summary>
    public static string CompareForm(MarkFieldKey field, string? raw)
    {
        var normalized = Normalize(field, raw).Value;
        if (field is MarkFieldKey.GrossWeight or MarkFieldKey.NetWeight)
        {
            // 重量比对时把单位剥掉，只比数字：25.5 KGS 与 25.5 应判为一致，
            // 而「缺单位」这件事由 Normalize 单独告警，不在此处混进来。
            return TextNormalizer.Compact(TextNormalizer.Numbers(normalized).FirstOrDefault());
        }

        return TextNormalizer.Compact(normalized);
    }

    private static NormalizedField NormalizeText(MarkFieldKey field, string text)
    {
        if (field == MarkFieldKey.BoxSize)
        {
            var size = SizePattern.Match(text);
            if (size.Success)
            {
                var unit = size.Groups[4].Success ? size.Groups[4].Value.ToUpperInvariant() : "CM";
                var parts = new[] { size.Groups[1].Value, size.Groups[2].Value, size.Groups[3].Value };
                var bad = parts.FirstOrDefault(p => !InRange(double.Parse(p, CultureInfo.InvariantCulture), 1, 5000));
                if (bad is not null)
                    return new NormalizedField($"{parts[0]}×{parts[1]}×{parts[2]} {unit}", $"外箱尺寸有不在 1~5000 范围内的数（{bad}），请核对");
                return new NormalizedField($"{parts[0]}×{parts[1]}×{parts[2]} {unit}", null);
            }

            return new NormalizedField(text, "没认出「长×宽×高」形状的尺寸，按原文保留，请核对");
        }

        if (text.Length > MaxTextLength)
        {
            return new NormalizedField(
                text[..MaxTextLength],
                $"内容超过 {MaxTextLength} 字符，可能被整行粘进了别的内容，已截断，请核对");
        }

        return new NormalizedField(text, null);
    }

    private static NormalizedField NormalizeInteger(MarkFieldKey field, string text)
    {
        // 件号最常见的形状是 "No. 3 / 12"：本箱号取前段，总件数取后段。
        var split = CartonSplitPattern.Match(text);
        if (split.Success)
        {
            var wanted = field == MarkFieldKey.CartonTotal ? split.Groups[2].Value : split.Groups[1].Value;
            return new NormalizedField(wanted, null);
        }

        var numbers = TextNormalizer.Numbers(text);
        if (numbers.Count == 0) return new NormalizedField(text, "这个字段要求整数，但识别结果里没有数字，请核对");

        // 整数字段里出现真正的字母（如 MM2603、A100）说明标签锚断了或模型编了值：
        // 这一护栏原先只在规则通道（RuleFieldExtractor）有，LLM 通道能直接抽出一个看着正常的数（批次一-10）。
        // 先剥掉常见的前缀标签再判（Ctn No. 3 / 12 是合法数据），且只看 ASCII 字母（"5件" 不能被误伤）。
        if (HasStrayAsciiLetter(text))
            return new NormalizedField(text, "整数字段里出现了字母（不是 Ctn No. 这类标签），可能是锚错或模型编的，请人工改成本箱号");

        // 一件号格里出现两个以上数字（如 "3 / 12"）时取第一个，并留话说明，避免静默选错。
        var first = numbers[0];
        var dot = first.IndexOf('.');
        if (dot >= 0)
        {
            var truncated = first[..dot];
            return new NormalizedField(truncated, $"这个字段应是整数，识别到 {first}，已取 {truncated}，请核对");
        }

        if (!int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return new NormalizedField(first, "数字太长，超出可打印范围，请核对");

        if (value < 0) return new NormalizedField(first, "出现负数，唛头上不该有负件数，请核对");
        if (numbers.Count > 1) return new NormalizedField(first, $"同一处读到多个数字（{string.Join(" / ", numbers)}），已取第一个，请核对");
        return new NormalizedField(first, null);
    }

    /// <summary>件号格里合法的英文前缀标签（可以连缀，如 "Ctn No. 3 / 12"；这些字母不算「值里的字母」）。</summary>
    private static readonly Regex CartonLabelPrefix = new(
        @"^(?:\s*(?:no\.?|nos\.?|ctn\.?|ctns\.?|carton\.?|box\.?|cases?\.?|of)[\s.:／/-]*)+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>剥掉件号前缀标签后，剩下的文本里是否还有 ASCII 字母（CJK 单位如「5件」不算）。</summary>
    private static bool HasStrayAsciiLetter(string text)
    {
        var residue = CartonLabelPrefix.Replace(text, string.Empty);
        return residue.Any(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
    }

    private static NormalizedField NormalizeWeight(MarkFieldKey field, string text)
    {
        var label = field == MarkFieldKey.GrossWeight ? "毛重" : "净重";
        var unitHit = WeightUnit.Match(text);
        var unit = unitHit.Success ? NormalizeWeightUnit(unitHit.Value) : string.Empty;

        // 从单位往左取数字，而不是从左往右扫第一个数：
        // 后者在本机真实样本上会把 "25 ． 5 KGS" 读成 5 KGS，把 25.5 静默印成 5。
        var head = unitHit.Success ? text[..unitHit.Index] : text;
        var numbers = TextNormalizer.Numbers(head);
        if (numbers.Count == 0) return new NormalizedField(text, $"{label}没读到数字，请核对");

        var number = numbers[^1];
        var atNumber = head.IndexOf(number, StringComparison.Ordinal);
        var negative = atNumber > 0 && (head[atNumber - 1] == '-' || head[atNumber - 1] == '−');

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return new NormalizedField(text, $"{label}数字读不出来，请核对");

        if (negative || value <= 0)
            return new NormalizedField(text, $"{label}是 0 或负数（{(negative ? "-" : string.Empty)}{number}{(unit.Length > 0 ? " " + unit : string.Empty)}），唛头上不可能，请核对");

        var ceiling = unit == "LBS" ? 8000 : 4000;
        // InvariantCulture:这是要印上唛头的文本,逗号小数点的系统区域不得把 25.5 印成 25,5(第 23 棒)
        var digits = value.ToString("0.###", CultureInfo.InvariantCulture);
        var canonical = unit.Length == 0 ? digits : $"{digits} {unit}";

        if (value > ceiling)
            return new NormalizedField(canonical, $"{label} {canonical} 超出合理范围（>{ceiling}），请核对");
        if (unit.Length == 0)
            return new NormalizedField(canonical, $"{label}没读到单位，打印出去容易被人当公斤或磅误用，请补上单位");

        return new NormalizedField(canonical, null);
    }

    private static string NormalizeWeightUnit(string raw)
    {
        var unit = TextNormalizer.Compact(raw);
        return unit switch
        {
            "kg" or "kgs" or "kilogram" or "kilograms" or "千克" or "公斤" => "KGS",
            "lb" or "lbs" or "pound" or "pounds" or "磅" => "LBS",
            _ => string.Empty,
        };
    }

    private static NormalizedField NormalizeVolume(MarkFieldKey field, string text)
    {
        if (SizePattern.IsMatch(text))
            return new NormalizedField(text, "这更像外箱尺寸（长×宽×高）不是体积，请核对是不是填错列了");

        var numbers = TextNormalizer.Numbers(text);
        if (numbers.Count == 0) return new NormalizedField(text, "没读到体积数字，请核对");

        if (!double.TryParse(numbers[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return new NormalizedField(text, "体积数字读不出来，请核对");

        if (value <= 0) return new NormalizedField(text, "体积为 0 或负数，不可能，请核对");
        if (value > 500)
        {
            var big = value.ToString("0.###", CultureInfo.InvariantCulture);
            return new NormalizedField($"{big} CBM", $"体积 {big} CBM 偏大（超过 500），请核对");
        }

        return new NormalizedField($"{value.ToString("0.###", CultureInfo.InvariantCulture)} CBM", null);
    }

    private static NormalizedField NormalizeDate(string text)
    {
        if (DateTime.TryParseExact(text, ExactDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return new NormalizedField(exact.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);

        // 斜杠日期是重灾区：美式写 M/d/yyyy、其他地方写 d/M/yyyy，两边都可能合法。
        var slash = Regex.Match(text, @"^(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{2}|\d{4})$");
        if (slash.Success
            && int.TryParse(slash.Groups[1].Value, out var a)
            && int.TryParse(slash.Groups[2].Value, out var b)
            && int.TryParse(slash.Groups[3].Value, out var year))
        {
            if (year < 100) year += year < 70 ? 2000 : 1900;

            if (a > 12 && b <= 12) return new NormalizedField(Format(year, b, a), null);
            if (b > 12 && a <= 12) return new NormalizedField(Format(year, a, b), null);
            if (a > 12 && b > 12) return new NormalizedField(text, "两段数字都不像月份，日期读不出来，请核对");

            // 1/2 与 2/1 都合法——工具替用户猜一个就是编数据，这里明确按美式读并说清楚。
            return new NormalizedField(Format(year, a, b), "月/日两处都可能，按美式 M/d/yyyy 读的，请核对");

            static string Format(int y, int m, int d) =>
                new DateOnly(y, m, d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // 带“年”的中文写法自己解（下面），别交给当前区域设置的（它可能把 2026年 拆成莫名其妙的结果）。
        if (!text.Contains('年')
            && DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var loose)
            && loose != default)
        {
            return new NormalizedField(loose.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
        }

        var chinese = Regex.Match(text, @"^(\d{4})年(\d{1,2})月(?:(\d{1,2})日)?$");
        if (chinese.Success
            && int.TryParse(chinese.Groups[1].Value, out var cy)
            && int.TryParse(chinese.Groups[2].Value, out var cm)
            && cm is >= 1 and <= 12)
        {
            var cd = chinese.Groups[3].Success ? int.Parse(chinese.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
            if (cd is >= 1 and <= 31)
            {
                return new NormalizedField(
                    new DateOnly(cy, cm, cd).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    chinese.Groups[3].Success ? null : "只读到年月，日按 1 号算，请核对");
            }
        }

        return new NormalizedField(text, "日期格式没认出来，按原文保留，请核对");
    }

    private static bool InRange(double value, double min, double max) => value >= min && value <= max;
}
