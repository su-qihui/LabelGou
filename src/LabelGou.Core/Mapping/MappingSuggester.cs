using System.Globalization;
using System.Text.RegularExpressions;
using LabelGou.Core.Data;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Mapping;

/// <summary>
/// 自动推荐"列 → 唛头字段"映射。
/// <para>
/// 打印店操作员不懂"字段"这套概念，所以默认替他把 obviously 的连好，他只需检查错的地方。
/// 打分依据是 <see cref="MarkFieldCatalog"/> 里的中英文别名——工厂表格大多是中英混排，
/// 这一层命中率直接决定工具好不好用。
/// </para>
/// </summary>
public static class MappingSuggester
{
    /// <summary>按表头生成一份自动映射好的方案。</summary>
    public static MappingProfile Suggest(IReadOnlyList<string> headers, string profileName = "自动匹配方案")
    {
        var profile = MappingProfile.CreateFor(headers, profileName);

        // 先算出所有候选 (字段, 列, 分数)，再按分数从高到低贪心分配，保证一个字段只占一列、一列只喂一个字段
        var candidates = new List<(MarkFieldKey Field, int Column, double Score, string Reason)>();

        for (var c = 0; c < headers.Count; c++)
        {
            var normalized = HeaderRowDetector.Normalize(headers[c]);
            if (normalized.Length == 0) continue;

            foreach (var def in MarkFieldCatalog.All)
            {
                var (score, reason) = ScoreHeader(normalized, def);
                if (score > 0) candidates.Add((def.Key, c, score, reason));
            }
        }

        var usedFields = new HashSet<MarkFieldKey>();
        var usedColumns = new HashSet<int>();

        foreach (var candidate in candidates.OrderByDescending(x => x.Score).ThenBy(x => x.Column))
        {
            if (!usedFields.Add(candidate.Field)) continue;
            if (!usedColumns.Add(candidate.Column)) continue;
            profile.Bind(candidate.Field, candidate.Column, headers);
        }

        return profile;
    }

    /// <summary>
    /// 在已保存的方案里找最贴合当前表头的一份：签名完全一致直接命中，否则按列标题重合度择优。
    /// </summary>
    public static MappingProfile? FindBestMatch(IReadOnlyList<string> headers, IEnumerable<MappingProfile> knownProfiles)
    {
        var signature = MappingProfile.ComputeSignature(headers);
        var list = knownProfiles.ToList();
        if (list.Count == 0) return null;

        var exact = list.FirstOrDefault(p => p.HeaderSignature == signature);
        if (exact is not null) return exact;

        MappingProfile? best = null;
        var bestOverlap = 0d;
        var current = headers.Select(HeaderRowDetector.Normalize).Where(h => h.Length > 0).ToHashSet();
        if (current.Count == 0) return null;

        foreach (var profile in list)
        {
            var saved = profile.HeaderHints.Select(HeaderRowDetector.Normalize).Where(h => h.Length > 0).ToList();
            if (saved.Count == 0) continue;
            var hit = saved.Count(current.Contains);
            var overlap = hit / (double)Math.Max(saved.Count, current.Count);
            if (overlap > bestOverlap && overlap >= 0.6)
            {
                bestOverlap = overlap;
                best = profile;
            }
        }
        if (best is null) return null;

        // 命中旧方案但列顺序可能变了：按列标题重连下标
        var rebound = MappingProfile.CreateFor(headers, best.Name);
        rebound.Note = best.Note;
        rebound.AutoNumberCartons = best.AutoNumberCartons;
        rebound.Numbering = best.Numbering;
        rebound.HeaderSignature = signature;

        foreach (var mapping in best.Mappings)
        {
            if (!mapping.IsBound) continue;
            var header = mapping.ColumnHeader ?? (mapping.ColumnIndex < best.HeaderHints.Count ? best.HeaderHints[mapping.ColumnIndex] : null);
            if (header is null) continue;

            var index = IndexOfHeader(headers, header);
            if (index >= 0) rebound.Bind(mapping.Field, index, headers);
        }
        return rebound;
    }

    private static int IndexOfHeader(IReadOnlyList<string> headers, string header)
    {
        var exact = headers.ToList().FindIndex(h => string.Equals(h, header, StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;
        var normalized = HeaderRowDetector.Normalize(header);
        return headers.ToList().FindIndex(h => HeaderRowDetector.Normalize(h) == normalized);
    }

    private static (double Score, string Reason) ScoreHeader(string normalizedHeader, FieldDefinition def)
    {
        // 完全等于别名 / 中文名 / 英文标记
        if (string.Equals(normalizedHeader, HeaderRowDetector.Normalize(def.ChineseName), StringComparison.Ordinal))
            return (1.0, "中文名完全一致");

        if (def.EnglishLabel.Length > 1
            && string.Equals(normalizedHeader, HeaderRowDetector.Normalize(def.EnglishLabel), StringComparison.Ordinal))
            return (1.0, "英文标记完全一致");

        foreach (var alias in def.Aliases)
        {
            var na = HeaderRowDetector.Normalize(alias);
            if (na.Length == 0) continue;
            if (normalizedHeader == na) return (0.98, "别名完全一致");
        }

        // 包含关系：列标题里含别名（如 "毛重G.W.(kg)"），或别名含列标题（如表头只写 "合同"）
        foreach (var alias in def.Aliases)
        {
            var na = HeaderRowDetector.Normalize(alias);
            if (na.Length < 2) continue;
            if (normalizedHeader.Contains(na, StringComparison.Ordinal)) return (0.72, "列标题包含别名");
            if (na.Contains(normalizedHeader, StringComparison.Ordinal) && normalizedHeader.Length >= 2)
                return (0.62, "别名包含列标题");
        }

        var chinese = HeaderRowDetector.Normalize(def.ChineseName);
        if (chinese.Length >= 2)
        {
            if (normalizedHeader.Contains(chinese, StringComparison.Ordinal)) return (0.7, "列标题含中文名");
            if (chinese.Contains(normalizedHeader, StringComparison.Ordinal) && normalizedHeader.Length >= 2)
                return (0.55, "中文名含列标题");
        }

        return (0, string.Empty);
    }
}

/// <summary>
/// 按映射方案把表格行转换成 <see cref="MarkRecord"/> 集合，并对数字类字段做量纲白名单校验。
/// <para>
/// 校验的原则：印刷数据宁标红也别默默印错。这里产出的 <see cref="MappingIssue"/>
/// 会在界面列出，Error 级别的字段会带 <see cref="MarkValue.Warning"/>，
/// 由 M3 打印前的"未核对不得打印"闸门消费。
/// </para>
/// </summary>
public static class RecordMapper
{
    private static readonly Regex NumberPattern = new(
        @"[-+]?\d+(?:[.,]\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static MappingResult Map(TabularData data, MappingProfile profile)
    {
        var records = new List<MarkRecord>(data.RowCount);
        var issues = new List<MappingIssue>();
        var bound = profile.BoundColumns();
        var total = data.RowCount;

        for (var r = 0; r < data.RowCount; r++)
        {
            var rowNumber = r + 1;
            var builder = MarkRecord.Builder().SetRow(rowNumber, $"{data.SheetName} 第 {data.HeaderRowIndex + 2 + r} 行");

            foreach (var mapping in profile.Mappings)
            {
                if (!mapping.IsBound) continue;
                var raw = data.GetCell(r, mapping.ColumnIndex);
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var def = MarkFieldCatalog.Get(mapping.Field);
                var value = new MarkValue(raw, ValueOrigin.ExcelImport)
                {
                    SourceRef = $"第 {HeaderRowDetector.ColumnLetter(mapping.ColumnIndex)} 列「{data.Headers[mapping.ColumnIndex]}」",
                };

                var problem = Validate(def, value.Text);
                if (problem is not null)
                {
                    var (severity, message) = problem.Value;
                    issues.Add(new MappingIssue(rowNumber, mapping.Field, message, severity));
                    value = new MarkValue(value.Text, value.Origin)
                    {
                        SourceRef = value.SourceRef,
                        Warning = message,
                        NeedsReview = severity == IssueSeverity.Error,
                    };
                }
                builder.Set(mapping.Field, value);
            }

            // 未映射的列也留下，模板里可用 {{col:列标题}} 引用（唛头常有客户自定义行）
            for (var c = 0; c < data.ColumnCount; c++)
            {
                if (bound.Contains(c)) continue;
                var raw = data.GetCell(r, c);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                builder.SetCustom("col:" + data.Headers[c], raw);
            }

            // 件号补齐：数据里没有就按行序推（来源标 Rule，与导入数据区分，便于界面注明"本工具生成"）
            if (profile.AutoNumberCartons)
            {
                if (!profile.Mappings.Any(m => m.Field == MarkFieldKey.CartonNo && m.IsBound))
                {
                    builder.Set(MarkFieldKey.CartonNo, rowNumber.ToString(CultureInfo.InvariantCulture), ValueOrigin.Rule);
                }
                if (!profile.Mappings.Any(m => m.Field == MarkFieldKey.CartonTotal && m.IsBound) && total > 1)
                {
                    builder.Set(MarkFieldKey.CartonTotal, total.ToString(CultureInfo.InvariantCulture), ValueOrigin.Rule);
                }
            }

            records.Add(builder.Build());
        }

        return new MappingResult(records, issues);
    }

    /// <summary>校验一个字段值；返回 null 表示没问题。</summary>
    private static (IssueSeverity, string)? Validate(FieldDefinition def, string text)
    {
        if (!def.Numeric)
        {
            return text.Length > 120 ? (IssueSeverity.Info, $"「{def.ChineseName}」内容较长（{text.Length} 字），打印时可能被截断") : null;
        }

        if (!TryExtractNumber(text, out var number))
        {
            return (IssueSeverity.Warning, $"「{def.ChineseName}」需要数字，但读到「{Trim(text)}」");
        }

        if (number < 0) return (IssueSeverity.Error, $"「{def.ChineseName}」为负数（{Trim(text)}），请核对");
        if (number == 0 && def.Kind is MarkValueKind.Weight or MarkValueKind.Volume)
            return (IssueSeverity.Warning, $"「{def.ChineseName}」为 0（{Trim(text)}），请确认单位是否正确");

        // 毛/净重超 1000：极可能是把克当千克填了
        if (def.Kind == MarkValueKind.Weight && number > 1000)
            return (IssueSeverity.Warning, $"「{def.ChineseName}」达 {Trim(text)}，疑似单位用错（克 vs 千克）");

        return null;
    }

    /// <summary>从"12.5 kg"这类文本里抽出第一个数字。允许千分位逗号。</summary>
    public static bool TryExtractNumber(string text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var match = NumberPattern.Match(text);
        if (!match.Success) return false;

        var token = match.Value.Replace(',', '.');      // "12,5" → 12.5；"1,234" 这类千分位按小数处理不了，交下面兜底
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;

        // 兜底：去掉所有非数字字符再试一次
        var digits = new string(match.Value.Where(ch => char.IsDigit(ch) || ch == '.' || ch == '-').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Trim(string text)
        => text.Length <= 24 ? text : text[..24] + "…";
}
