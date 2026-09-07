using LabelGou.Core.Units;

namespace LabelGou.Core.Templates;

/// <summary>
/// 行式骨架里的一行文字。
/// <para><see cref="Stretch"/> 为真时字号由行带高度反算（大字唛头就是这么来的），
/// 为假时用 <see cref="SizePt"/>（明细行、备注行）。</para>
/// </summary>
public sealed class RowSpec
{
    /// <summary>行内容，可含 <c>{{字段键}}</c> 占位符与固定文字混排（如 <c>QTY：{{Quantity}} pcs</c>）。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>行高权重。金沐那张「客户名比明细行高一截」就是靠 1.6 : 1 : 1 : 1 表达的。</summary>
    public double Weight { get; set; } = 1;

    public HorizontalAlign Align { get; set; } = HorizontalAlign.Left;

    /// <summary>不撑满时用的字号（磅）。</summary>
    public double SizePt { get; set; } = 12;

    /// <summary>撑满本行带宽（真厂商样张里的大字行）。</summary>
    public bool Stretch { get; set; }

    public bool Bold { get; set; } = true;

    public string FontFamily { get; set; } = TemplateElement.DefaultFont;
}

/// <summary>
/// 行式版式骨架：<strong>唛头 = 若干行文字</strong>，行高按权重分、大字行撑满本行带宽。
/// <para>
/// 为什么要有这个东西：<c>labelgou-CL</c> 里 5 家厂商的真样张，5 家都是行式排布
/// （金沐 4 行、邱总 4 行居中、郑小姐 2 行超大字、OLU 大字+三行明细、TOP logo+条码+5 行），
/// 而 M1 那三套内置箱唛模板是"框线分格"式，一家都对不上。行式骨架 + 权重 + 撑满
/// 就能不依赖 AI 覆盖掉绝大多数厂牌，AI（M7）要做的只是"从样张里把这几行认出来"。
/// </para>
/// <para>
/// 几何全部在这里算成毫米，App 层只负责画：<strong>骨架不碰像素，渲染不重算坐标</strong>（§七铁律）。
/// </para>
/// </summary>
public sealed class RowLayoutSpec
{
    /// <summary>撑满时"字高占行带"的比例。雅黑单行 <c>FormattedText.Height ≈ 1.35em</c>，取 0.74 让字面刚好填满一条。</summary>
    public const double StretchEmPerBand = 0.74;

    public string Id { get; set; } = "user.rows-" + Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "行式唛头";

    public string Note { get; set; } = string.Empty;

    public double WidthMm { get; set; } = 140;

    public double HeightMm { get; set; } = 100;

    /// <summary>四周留白（毫米）。印刷常用 3~6。</summary>
    public double PaddingMm { get; set; } = 5;

    /// <summary>行间距（毫米）。</summary>
    public double GapMm { get; set; } = 2;

    /// <summary>是否画外框。真样张里只有 TOP/OLU 有边框，金沐与郑小姐是纯文字无边。</summary>
    public bool DrawBorder { get; set; }

    public double BorderMm { get; set; } = 0.5;

    public List<RowSpec> Rows { get; } = new();

    /// <summary>
    /// 算成一份 <see cref="LabelTemplate"/>。行带高度按权重分配，剩余高度分给行距。
    /// <para>行数 0、或留白/行距把版面吃光（可用高 ≤ 0）时返回 null：宁可让调用方看到"排不出来"，
    /// 也不产出一份会越界的模板。</para>
    /// </summary>
    public LabelTemplate? Build()
    {
        if (Rows.Count == 0) return null;

        var usableW = WidthMm - PaddingMm * 2;
        var usableH = HeightMm - PaddingMm * 2 - GapMm * (Rows.Count - 1);
        if (usableW <= 1 || usableH <= 1) return null;

        var totalWeight = Rows.Sum(r => Math.Max(0.05, r.Weight));
        var perWeightMm = usableH / totalWeight;

        var template = new LabelTemplate
        {
            Id = Id,
            Name = Name,
            Note = Note,
            WidthMm = WidthMm,
            HeightMm = HeightMm,
            PaddingMm = PaddingMm,
            BorderMm = 0,       // 外框由下面的 Rect 显式画，避免两套边框来源
            CropMarkMm = 0,
        };

        if (DrawBorder)
        {
            template.Elements.Add(new TemplateElement
            {
                Kind = ElementKind.Rect,
                X = 0.4,
                Y = 0.4,
                Width = WidthMm - 0.8,
                Height = HeightMm - 0.8,
                ThicknessMm = BorderMm,
            });
        }

        var y = PaddingMm;
        foreach (var row in Rows)
        {
            var band = perWeightMm * Math.Max(0.05, row.Weight);
            var sizePt = row.Stretch
                ? Mm.MmToPoint(band * StretchEmPerBand)
                : row.SizePt;
            sizePt = Math.Clamp(sizePt, TemplateValidator.MinFontPt, TemplateValidator.MaxFontPt);

            template.Elements.Add(new TemplateElement
            {
                Kind = ElementKind.Text,
                X = PaddingMm,
                Y = y,
                Width = usableW,
                Height = band,
                Text = row.Content,
                FontFamily = row.FontFamily,
                FontSizePt = sizePt,
                Bold = row.Bold,
                Align = row.Align,
                // 撑满行只许一行：值变长时宁可缩字号，也不要换行后挤出这条带
                MaxLines = row.Stretch ? 1 : 3,
                ShrinkToFit = true,
            });
            y += band + GapMm;
        }

        return template;
    }
}
