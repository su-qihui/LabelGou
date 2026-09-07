using LabelGou.Core.Units;

namespace LabelGou.Core.Interop.Svg;

/// <summary>
/// 把「一个视觉行被拆成一堆单字」的底稿拼回整行。
/// <para>
/// 本机取证（<c>labelgou-CL\7.8 金沐 唛头\7.8金沐唛头.svg</c>，CorelDRAW X4 导出）：
/// 一行 <c>BOLAROM</c> 是七个独立 <c>&lt;text&gt;</c>（<c>x</c> 依次 70 / 85.656 / 103.538 …），
/// 更麻烦的是<strong>三个全角冒号被写在文件末尾</strong>，不在自己该在的行内位置。
/// 所以合并必须<strong>先按基线+字号分桶、再按映射后的 X 排序</strong>，绝不能按文档顺序串。
/// </para>
/// <para>
/// 不合并的后果不是"难看"，而是<strong>整条导入链路失效</strong>：字段识别靠"整行文字命中别名/样例数据"，
/// 单字 <c>B</c>、<c>O</c> 什么都命中不了，于是用户看到的就是"导进来了但没效果"。
/// </para>
/// </summary>
public static class SvgTextLineJoiner
{
    /// <summary>同一行的基线容差（倍字号）：小于此值算同一基线。</summary>
    public const double BaselineToleranceEm = 0.18;

    /// <summary>相邻字符合并的最大间距（倍字号）：再大就是列间空隙/两栏，不能缝成一行。</summary>
    public const double MaxGapEm = 0.45;

    /// <summary>字号相同才允许同桶（倍）：CDR 常用大小字号区分客户名与明细行。</summary>
    public const double SizeToleranceRatio = 0.02;

    /// <summary>
    /// 就地合并 <paramref name="document"/> 的文字元素，返回合并统计供导入窗口说人话。
    /// <para>只动 <see cref="SvgDocument.Texts"/>，路径与图片一律不碰。</para>
    /// </summary>
    public static JoinReport Join(SvgDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var original = document.Texts;
        // 先拿住合并前的个数：original 与 document.Texts 是同一个 List，Clear 之后再读就永远是“没合并”
        var before = original.Count;
        if (before < 2) return new JoinReport(before, before, 0);

        // 分桶条件：基线、字号、字体族、粗体、填充色全一致才进同一桶（见 SameLine）。
        // 分桶只解决“是不是同一行”，真正断行仍按逐元素的间距算，避免把相邻两行错缝一起。
        var buckets = new List<List<SvgText>>();
        foreach (var text in original)
        {
            var placed = false;
            foreach (var bucket in buckets)
            {
                // 跟桶里最后一个比而不是跟第一个比：长一行的基线常有轻微漂移，跟头比会越比越宽。
                if (SameLine(bucket[^1], text))
                {
                    bucket.Add(text);
                    placed = true;
                    break;
                }
            }
            if (!placed) buckets.Add(new List<SvgText> { text });
        }

        var merged = new List<SvgText>();
        var glued = 0;
        foreach (var bucket in buckets.OrderBy(b => b[0].BaselineYmm).ThenBy(b => b[0].XMm))
        {
            var ordered = bucket.OrderBy(t => t.XMm).ThenBy(t => t.BaselineYmm).ToList();
            var run = new List<SvgText> { ordered[0] };
            for (var i = 1; i < ordered.Count; i++)
            {
                var prev = ordered[i - 1];
                var cur = ordered[i];
                if (cur.XMm - RightEdge(prev) <= MaxGapEm * Mm.PointToMm(cur.SizePt))
                {
                    run.Add(cur);
                    glued++;
                    continue;
                }

                merged.Add(Flatten(run));
                run = new List<SvgText> { cur };
            }
            merged.Add(Flatten(run));
        }

        document.Texts.Clear();
        foreach (var text in merged) document.Add(text);
        return new JoinReport(before, merged.Count, glued);
    }

    /// <summary>右端（毫米）：优先用已算好的包围盒，退化时用估算宽度。</summary>
    private static double RightEdge(SvgText text)
        => !text.Bounds.IsEmpty ? text.Bounds.MaxX : text.XMm + text.WidthEstimateMm;

    private static bool SameLine(SvgText a, SvgText b)
    {
        var sizePt = Math.Max(a.SizePt, b.SizePt);
        if (Math.Abs(a.SizePt - b.SizePt) > sizePt * SizeToleranceRatio) return false;
        // 基线是毫米，字号是磅——两者不能直接比！先折成毫米（这个单位差曾把隔 2.5mm 的 70 行缝成 1 行）
        var sizeMm = Mm.PointToMm(sizePt);
        if (Math.Abs(a.BaselineYmm - b.BaselineYmm) > sizeMm * BaselineToleranceEm) return false;
        if (!string.Equals(a.FontFamily, b.FontFamily, StringComparison.OrdinalIgnoreCase)) return false;
        // 刻意不比粗体：真样本里「Ctns：5」是 bold、「件」是同一行的 normal 后缀，
        // 要求同粗体就会把一行拆成两行。合并后统一取第一段的字重，导入后人在编辑器里能改。
        return string.Equals(a.Fill?.Color ?? "#000000", b.Fill?.Color ?? "#000000", StringComparison.OrdinalIgnoreCase);
    }

    private static SvgText Flatten(IReadOnlyList<SvgText> run)
    {
        if (run.Count == 1) return run[0];

        var first = run[0];
        var content = new System.Text.StringBuilder();
        SvgBounds bounds = SvgBounds.Empty;
        var ids = new List<string>();
        foreach (var piece in run)
        {
            content.Append(piece.Content);
            bounds = bounds.IsEmpty ? piece.Bounds : bounds.Union(piece.Bounds);
            if (piece.Id is not null) ids.Add(piece.Id);
        }

        var left = Math.Min(
            run.Min(t => t.XMm),
            bounds.IsEmpty ? double.MaxValue : bounds.XMm);
        if (double.IsInfinity(left)) left = first.XMm;

        // 源文件里每段各自带 id，被提升成可编辑元素时要从底图里全部剔掉，否则固定文字会印两遍。
        return new SvgText
        {
            Content = content.ToString(),
            XMm = left,
            BaselineYmm = run.Average(t => t.BaselineYmm),
            SizePt = first.SizePt,
            FontFamily = first.FontFamily,
            Bold = first.Bold,
            Anchor = SvgTextAnchor.Start,
            Fill = first.Fill,
            Id = first.Id,
            MergedSourceIds = ids.Count > 0 ? ids : null,
            WidthEstimateMm = bounds.IsEmpty ? run.Sum(t => t.WidthEstimateMm) : bounds.WidthMm,
            Bounds = bounds,
            SourceTag = first.SourceTag,
            MergedFromCount = run.Count,
        };
    }

    /// <summary>合并统计。<see cref="After"/> 远小于 <see cref="Before"/> 说明这份底稿是被逐字拆开的。</summary>
    public readonly record struct JoinReport(int Before, int After, int GluedPieces)
    {
        public int LinesSaved => Before - After;

        public string Describe() => Before == After
            ? $"文字元素 {Before} 个，本来就是整行，未做合并。"
            : $"{Before} 段文字合并成 {After} 行（{GluedPieces} 处接缝）——这份底稿在源软件里是被逐字拆开的。";
    }
}
