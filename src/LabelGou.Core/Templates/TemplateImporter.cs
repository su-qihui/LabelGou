using LabelGou.Core.Editing;
using LabelGou.Core.Interop.Cdr;
using LabelGou.Core.Interop.Svg;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Templates;

/// <summary>底稿的来源形态。</summary>
public enum TemplateImportSource
{
    /// <summary>CDR/AI/Inkscape 导出的 SVG：能拿到保真矢量底图与文字。</summary>
    Svg = 0,

    /// <summary>直接给的 <c>.cdr</c>：只能拿到内嵌缩略图，当不可打印的参考底图。</summary>
    CdrPreview = 1,
}

/// <summary>
/// 底稿里一个可以"提升为可编辑文字元素"的候选。
/// <para><see cref="Promote"/> 与 <see cref="Field"/> 是<strong>可写的</strong>——导入窗口里由用户勾选与改绑，
/// 自动判定的结果只是预勾，不当结论（§七-7：数据严谨，不猜着绑）。</para>
/// </summary>
public sealed class TextCandidate
{
    /// <summary>在底稿 <c>Texts</c> 里的下标（从底图里剔除该节点时要用它定位）。</summary>
    public required int DocumentIndex { get; init; }

    public required string Content { get; init; }

    /// <summary>源节点 id；SVG 里没有 id 时为 null，此时按序号剔除。</summary>
    public string? SourceId { get; init; }

    public double XMm { get; init; }
    public double YMm { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
    public double SizePt { get; init; }
    public string FontFamily { get; init; } = TemplateElement.DefaultFont;
    public bool Bold { get; init; }
    public HorizontalAlign Align { get; init; } = HorizontalAlign.Left;

    /// <summary>是否提升为可编辑元素（默认按三信号预勾，用户可改）。</summary>
    public bool Promote { get; set; }

    /// <summary>绑定到哪个唛头字段；null 表示当固定文字（导入时按原文写死，用户可在编辑器里改成占位符）。</summary>
    public MarkFieldKey? Field { get; set; }

    /// <summary>为什么预勾/为什么不预勾（窗口里要给人看懂的一句话）。</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>自动判定置信度（0~1）。0 表示纯人工判断。</summary>
    public double Confidence { get; set; }

    public string DisplayText => Content.Length <= 26 ? Content : Content[..26] + "…";
}

/// <summary>
/// 一次底稿导入的完整方案：底稿数据 + 文字候选 + 用户可调的标签尺寸，最后 <see cref="Build"/> 成模板。
/// <para>这个类是"先看清楚再落库"的那一层：<c>TemplateStore.Save</c> 只认已经建好的模板，
/// 而导入过程中的所有取舍（哪些文字提升、底图怎么落）都留在这里并可被界面改。</para>
/// </summary>
public sealed class TemplateImportPlan
{
    /// <summary>可提升文字的上限：留够余量给底图与用户自己后加的字段（<see cref="TemplateValidator.MaxElements"/> 是 80）。</summary>
    public const int PromotableLimit = 60;

    public required TemplateImportSource Source { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>SVG 底稿的解析结果；<c>.cdr</c> 路线为 null。</summary>
    public SvgDocument? Document { get; init; }

    /// <summary>从 <c>.cdr</c> 抠出来的预览图；SVG 路线为 null。</summary>
    public CdrPreview? Preview { get; init; }

    /// <summary>解析/读取过程中的告警与降级说明。</summary>
    public List<TemplateIssue> Issues { get; } = new();

    /// <summary>标签宽（毫米）。默认取底稿画布尺寸，用户可改（改成别的尺寸时底图按比例居中适配）。</summary>
    public double LabelWidthMm { get; set; } = 100;

    public double LabelHeightMm { get; set; } = 80;

    public List<TextCandidate> Texts { get; } = new();

    /// <summary>底稿是否带可打印的矢量底图。</summary>
    public bool HasVectorBackground => Document is { Paths.Count: > 0 } || Document is { Images.Count: > 0 };

    /// <summary>底稿里有多少文字被预勾为可编辑。</summary>
    public int PromotedCount => Texts.Count(t => t.Promote);

    public string SourceName => System.IO.Path.GetFileName(SourcePath);

    /// <summary>给界面顶部那段"我读到了什么"的人话摘要。</summary>
    public IReadOnlyList<string> SummaryLines()
    {
        var lines = new List<string>
        {
            Source == TemplateImportSource.Svg
                ? $"SVG 底稿：{SourceName}，画布 {LabelWidthMm:0.#} × {LabelHeightMm:0.#} mm，" +
                  $"几何 {Document?.Paths.Count ?? 0} 项、文字 {Document?.Texts.Count ?? 0} 段、位图 {Document?.Images.Count ?? 0} 张（已折算成毫米，1 用户单位 = {(Document?.UserUnitMm ?? 0):0.####} mm）"
                : $"CorelDRAW 底稿：{SourceName}，只取到内嵌预览图 {Preview?.Width}×{Preview?.Height} 像素（{CdrPreviewReader.DescribeVersion(Preview?.VersionHint)}）",
        };

        if (Source == TemplateImportSource.CdrPreview && Preview is not null)
        {
            var dpi = Preview.EquivalentDpiAt(LabelWidthMm);
            lines.Add($"这张预览图铺满 {LabelWidthMm:0.#} mm 宽时只有约 {dpi:0} DPI，只当对参考用、不会印到纸上（已标为参考图）。");
            lines.Add("要拿到保真底图：在 CorelDRAW 里「文件 → 导出 → SVG」，再导进来。tools\\cdr 里有批量导出宏。");
        }
        else
        {
            lines.Add($"文字候选 {Texts.Count} 段，其中 {PromotedCount} 段预勾为可编辑元素（其余留在底图里照常印刷）。");
        }

        lines.AddRange(Issues.Where(i => i.Severity != IssueLevel.Info).Select(i => i.Message));
        return lines;
    }

    /// <summary>
    /// 生成底图 SVG 文本：底稿原文减去被提升走的那些文字（定案 D6，避免同一行字画两遍）。
    /// <c>.cdr</c> 路线返回 null。
    /// </summary>
    public string? BuildBackgroundSvg()
    {
        if (Document is null) return null;
        var promoted = new HashSet<string>(StringComparer.Ordinal);
        var promotedIndexes = new HashSet<int>();
        foreach (var candidate in Texts.Where(t => t.Promote))
        {
            if (candidate.SourceId is not null) promoted.Add(candidate.SourceId);
            promotedIndexes.Add(candidate.DocumentIndex);
        }

        var kept = new SvgDocument
        {
            WidthMm = Document.WidthMm,
            HeightMm = Document.HeightMm,
            UserUnitMm = Document.UserUnitMm,
        };
        kept.Paths.AddRange(Document.Paths);
        kept.Images.AddRange(Document.Images);
        for (var i = 0; i < Document.Texts.Count; i++)
        {
            var text = Document.Texts[i];
            // 有 id 按 id 剔；没 id（CDR 导出很常见）只能按序号剔，两种都认才不漏
            if (text.Id is not null ? promoted.Contains(text.Id) : promotedIndexes.Contains(i)) continue;
            kept.Texts.Add(text);
        }
        return SvgWriter.WriteDocument(kept, new SvgWriteOptions
        {
            Comment = $"由 LabelGou 从底稿 {SourceName} 生成（已剔除提升为可编辑元素的所有文字）",
        });
    }

    /// <summary>
    /// 落成模板：<strong>底图占 1 个元素位</strong>，提升的文字各占 1 个，全部收进标签内。
    /// 资源文件经 <see cref="TemplateStore.SaveAsset(string,string)"/> 落进 <c>assets\</c>。
    /// </summary>
    public (LabelTemplate Template, IReadOnlyList<TemplateIssue> Issues) Build(string templateName, TemplateStore store)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));

        var template = new LabelTemplate
        {
            Name = string.IsNullOrWhiteSpace(templateName) ? "底稿导入模板" : templateName.Trim(),
            BuiltIn = false,
            WidthMm = Math.Clamp(LabelWidthMm, TemplateValidator.MinLabelSideMm, TemplateValidator.MaxLabelSideMm),
            HeightMm = Math.Clamp(LabelHeightMm, TemplateValidator.MinLabelSideMm, TemplateValidator.MaxLabelSideMm),
            PaddingMm = 4,
            // 黑稿自己带边框，再套一圈就成了双线框；这里明确关掉
            BorderMm = 0,
        };
        template.SchemaVersion = LabelTemplate.CurrentSchemaVersion;

        var issues = new List<TemplateIssue>(Issues);

        if (Source == TemplateImportSource.Svg && Document is not null)
        {
            var background = BuildBackgroundSvg();
            var hasAnything = Document.Paths.Count > 0 || Document.Images.Count > 0
                              || Document.Texts.Count > PromotedCount;
            if (background is not null && hasAnything)
            {
                var fileName = TemplateStore.SafeAssetName(template.Name) + ".bg.svg";
                var relative = store.SaveAsset(fileName, background);

                // 底图按自身画布比例铺进标签：用户改了标签尺寸时居中留边，绝不拉扁
                var fit = Math.Min(template.WidthMm / Math.Max(1e-6, Document.WidthMm),
                    template.HeightMm / Math.Max(1e-6, Document.HeightMm));
                if (fit <= 0 || double.IsNaN(fit) || double.IsInfinity(fit)) fit = 1;
                var drawWidth = Document.WidthMm * fit;
                var drawHeight = Document.HeightMm * fit;
                if (Math.Abs(fit - 1) > 0.001)
                {
                    issues.Add(new TemplateIssue(IssueLevel.Info,
                        $"标签尺寸（{template.WidthMm:0.#}×{template.HeightMm:0.#}mm）与底稿画布（{Document.WidthMm:0.#}×{Document.HeightMm:0.#}mm）不一致，" +
                        $"底图已按比例缩到 {fit * 100:0.#}% 并居中，没有拉伸变形。"));
                }

                var vector = new TemplateElement
                {
                    Kind = ElementKind.Vector,
                    ImagePath = relative,
                    X = (template.WidthMm - drawWidth) / 2,
                    Y = (template.HeightMm - drawHeight) / 2,
                    Width = drawWidth,
                    Height = drawHeight,
                };
                EditGeometry.ClampIntoLabel(template, vector);
                template.Elements.Add(vector);
            }
        }
        else if (Preview is not null)
        {
            // .cdr 路线：只有那张糊预览图，标成参考图，打印与导出都不带它
            var reference = new TemplateElement
            {
                Kind = ElementKind.Image,
                ImagePath = PreviewReferencePath,
                X = 0,
                Y = 0,
                Width = template.WidthMm,
                Height = template.HeightMm,
                ReferenceOnly = true,
            };
            if (!string.IsNullOrWhiteSpace(PreviewReferencePath))
            {
                EditGeometry.ClampIntoLabel(template, reference);
                template.Elements.Add(reference);
            }
        }

        foreach (var candidate in Texts.Where(t => t.Promote))
        {
            var element = new TemplateElement
            {
                Kind = ElementKind.Text,
                X = candidate.XMm,
                Y = candidate.YMm,
                Width = Math.Max(2, candidate.WidthMm),
                Height = Math.Max(2, candidate.HeightMm),
                FontFamily = candidate.FontFamily,
                FontSizePt = candidate.SizePt,
                Bold = candidate.Bold,
                Align = candidate.Align,
                // 一行的底稿文字不该被自动折成三行
                MaxLines = 1,
                ShrinkToFit = true,
                Text = candidate.Field is { } key
                    ? "{{" + key + "}}"
                    : candidate.Content,
            };
            EditGeometry.ClampIntoLabel(template, element);
            template.Elements.Add(element);
        }

        if (template.Elements.Count(t => t.Kind == ElementKind.Text) > 0 && !HasAnyPrintableContent(template))
            issues.Add(new TemplateIssue(IssueLevel.Warning, "这份模板的实际内容只有参考图，出片会是空白。"));

        issues.AddRange(TemplateValidator.Validate(template));
        return (template, issues);
    }

    /// <summary>
    /// <c>.cdr</c> 参考图落库后的相对路径。PNG 编码由 App 层做（Core 不碰 WPF 编码器），
    /// 所以这里只是个"等 App 填"的槽位：为 null 时不产出参考元素。
    /// </summary>
    public string? PreviewReferencePath { get; set; }

    private static bool HasAnyPrintableContent(LabelTemplate template)
        => template.BorderMm > 0 || template.Elements.Any(e => e.Visible && !e.ReferenceOnly);

    /// <summary>底稿里到底有没有可编辑内容（决定界面是否要提醒用户"这只是参考图"）。</summary>
    public bool NeedsManualFields => Source == TemplateImportSource.CdrPreview || Texts.Count == 0;
}

/// <summary>
/// C 类模板导入器：把外部底稿变成<strong>能过 <see cref="TemplateValidator"/> 的普通模板</strong>，
/// 不新建第二套模板模型（M4 编辑器直接就能改它）。
/// <para>
/// 文字提升的三信号（定案 D8）：<strong>只有信号 1 自动预勾</strong>——
/// ① 节点文字与样例记录的某字段值对得上 → 预勾并绑该字段；
/// ② 命中字段别名（"POD"、"G.W."、"合同号"…）→ <strong>不勾</strong>，这类通常是印死的标签文字；
/// ③ 其余 → 不勾，留在底图里照常印刷。用户在导入窗口里可以逐条改。
/// </para>
/// </summary>
public static class TemplateImporter
{
    /// <summary>样例记录：判定"这行文字是不是可变字段"的对照物。</summary>
    private static readonly MarkRecord Sample = SampleRecords.StandardSample();

    private static readonly Dictionary<string, string> SampleIndex = BuildSampleIndex();

    private static Dictionary<string, string> BuildSampleIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in MarkFieldCatalog.Mappable)
        {
            var value = Sample.GetText(definition.Key);
            var key = Normalize(value);
            if (key.Length < 2 || map.ContainsKey(key)) continue;
            map[key] = definition.Key.ToString();
        }
        return map;
    }

    /// <summary>读 SVG 底稿。</summary>
    public static TemplateImportPlan FromSvgFile(string path)
    {
        var parsed = SvgParser.ParseFile(path);
        var plan = new TemplateImportPlan
        {
            Source = TemplateImportSource.Svg,
            SourcePath = path,
            Document = parsed.Document,
            LabelWidthMm = ClampSide(parsed.Document.WidthMm),
            LabelHeightMm = ClampSide(parsed.Document.HeightMm),
        };
        plan.Issues.AddRange(parsed.Issues);

        if (parsed.HasError) return plan;

        for (var i = 0; i < parsed.Document.Texts.Count; i++)
            plan.Texts.Add(CandidateFor(parsed.Document.Texts[i], i));

        var over = plan.Texts.Count(t => t.Promote) - TemplateImportPlan.PromotableLimit;
        if (over > 0)
        {
            // 超上限时按置信度从低到高取消，别把最像字段的那些挤掉
            foreach (var candidate in plan.Texts.Where(t => t.Promote).OrderBy(t => t.Confidence).Take(over))
            {
                candidate.Promote = false;
                candidate.Reason = $"可编辑元素上限 {TemplateImportPlan.PromotableLimit} 个，这一行置信度偏低，已退回底图（可在窗口里手动勾回，但总数不能再超）";
            }
            plan.Issues.Add(new TemplateIssue(IssueLevel.Warning,
                $"底稿里有超过 {TemplateImportPlan.PromotableLimit} 行文字像可变字段，已按置信度保留最像的那些，其余留在底图里。"));
        }
        return plan;
    }

    /// <summary>读 <c>.cdr</c>：只取内嵌预览图。</summary>
    public static TemplateImportPlan FromCdrFile(string path)
    {
        var read = CdrPreviewReader.ReadFile(path);
        var plan = new TemplateImportPlan
        {
            Source = TemplateImportSource.CdrPreview,
            SourcePath = path,
            Preview = read.Preview,
        };
        plan.Issues.AddRange(read.Issues);
        if (read.Preview is null) return plan;

        // 预览图没有真实尺寸信息，标签尺寸只能先给个常见值，由用户在窗口里填
        plan.LabelWidthMm = 100;
        plan.LabelHeightMm = 80;
        plan.Issues.Add(new TemplateIssue(IssueLevel.Info,
            ".cdr 内嵌预览图不带真实尺寸，下面两个毫米数请照实际唛头填（它只决定标签大小，不会改变图像质量）。"));
        return plan;
    }

    /// <summary>SVG 是否值得一试（先看文件头，避免把 .cdr 当 SVG 解析后报一堆看不懂的错）。</summary>
    public static bool LooksLikeSvg(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var head = reader.Peek() switch
            {
                '<' => true,
                _ => false,
            };
            return head;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static TextCandidate CandidateFor(SvgText text, int index)
    {
        var normalized = Normalize(text.Content);
        var candidate = new TextCandidate
        {
            DocumentIndex = index,
            Content = text.Content,
            SourceId = text.Id,
            XMm = text.XMm,
            YMm = text.Bounds.IsEmpty ? text.BaselineYmm : text.Bounds.YMm,
            WidthMm = Math.Max(text.WidthEstimateMm, text.Bounds.WidthMm),
            HeightMm = text.Bounds.IsEmpty ? Core.Units.Mm.PointToMm(text.SizePt) * 1.25 : Math.Max(1, text.Bounds.HeightMm),
            SizePt = text.SizePt,
            FontFamily = text.FontFamily,
            Bold = text.Bold,
            Align = text.Anchor switch
            {
                SvgTextAnchor.Middle => HorizontalAlign.Center,
                SvgTextAnchor.End => HorizontalAlign.Right,
                _ => HorizontalAlign.Left,
            },
        };

        if (normalized.Length == 0)
        {
            candidate.Reason = "没有文字内容";
            return candidate;
        }

        // 信号 ①：与样例数据某字段的值对得上 → 预勾并绑字段
        if (SampleIndex.TryGetValue(normalized, out var exactKey) && TryParseField(exactKey, out var field))
        {
            candidate.Promote = true;
            candidate.Field = field;
            candidate.Confidence = 0.95;
            candidate.Reason = $"整段文字与样例数据的「{MarkFieldCatalog.Get(field).ChineseName}」一致，已绑成 {{{{{field}}}}}";
            return candidate;
        }

        var partial = MatchPartial(normalized);
        if (partial is not null && TryParseField(partial.Value.Key, out var partialField))
        {
            candidate.Promote = true;
            candidate.Field = partialField;
            candidate.Confidence = partial.Value.Confidence;
            candidate.Reason = $"文字里含有样例数据「{MarkFieldCatalog.Get(partialField).ChineseName}」的值，已绑字段（置信度 {partial.Value.Confidence:0.00}，请核对）";
            return candidate;
        }

        // 信号 ②：命中字段名/别名 → 这是印死的标签文字，绑了反而把固定文字变成死字段
        var label = AliasHit(text.Content);
        if (label is not null)
        {
            candidate.Confidence = 0;
            candidate.Reason = $"「{label}」是字段名标签（固定文字），留在底图里；要让它随数据变动请手动勾上并选字段";
            return candidate;
        }

        candidate.Confidence = 0;
        candidate.Reason = "与样例数据对不上，按固定文字留在底图里";
        return candidate;
    }

    private static (string Key, double Confidence)? MatchPartial(string normalized)
    {
        (string, double)? best = null;
        foreach (var definition in MarkFieldCatalog.Mappable)
        {
            var value = Normalize(Sample.GetText(definition.Key));
            if (value.Length < 5) continue;
            if (normalized.Length >= value.Length && normalized.Contains(value, StringComparison.Ordinal))
            {
                if (best is null || best.Value.Item2 < 0.8) best = (definition.Key.ToString(), 0.8);
                continue;
            }
            if (value.Length >= 6 && normalized.Length >= 6 && value.Contains(normalized, StringComparison.Ordinal))
            {
                if (best is null || best.Value.Item2 < 0.7) best = (definition.Key.ToString(), 0.7);
            }
        }
        return best;
    }

    private static string? AliasHit(string raw)
    {
        var normalized = Normalize(raw);
        if (normalized.Length == 0 || normalized.Length > 18) return null;
        foreach (var definition in MarkFieldCatalog.Mappable)
        {
            foreach (var alias in new[] { definition.EnglishLabel, definition.ChineseName }.Concat(definition.Aliases))
            {
                var key = Normalize(alias);
                if (key.Length < 2) continue;
                if (normalized == key || normalized.StartsWith(key, StringComparison.Ordinal))
                    return alias;
            }
        }
        return null;
    }

    private static bool TryParseField(string key, out MarkFieldKey field) => MarkFieldCatalog.TryParseKey(key, out field);

    private static double ClampSide(double mm)
        => double.IsNaN(mm) || mm <= 0
            ? 100
            : Math.Clamp(mm, TemplateValidator.MinLabelSideMm, TemplateValidator.MaxLabelSideMm);

    /// <summary>比对用：去空白与标点、转大写，"LOS ANGELES, USA" 与 "losangelesusa" 才算一个东西。</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var buffer = new char[text.Length];
        var length = 0;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (!char.IsLetterOrDigit(c)) continue;
            buffer[length++] = char.ToUpperInvariant(c);
        }
        return new string(buffer, 0, length);
    }
}
