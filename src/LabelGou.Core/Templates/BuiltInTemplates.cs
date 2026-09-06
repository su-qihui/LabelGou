namespace LabelGou.Core.Templates;

/// <summary>
/// A 类内置标准唛头模板（M1 交付：选模板 + 填数据即可用）。
/// <para>
/// 三套版式覆盖打印店最常见的三种不干胶规格：100×80 标准箱唛、60×40 精简小唛、
/// 120×90 中英对照全字段。坐标全部按毫米手工标定，且必须通过
/// <see cref="TemplateValidator.Validate"/>（有单元测试守住这条线）。
/// </para>
/// </summary>
public static class BuiltInTemplates
{
    /// <summary>全部内置模板。</summary>
    public static IReadOnlyList<LabelTemplate> All { get; } = new[]
    {
        Standard100x80(),
        Compact60x40(),
        Bilingual120x90(),
    };

    /// <summary>每次调用返回新实例，避免界面编辑时污染内置定义。</summary>
    public static LabelTemplate? GetById(string? id) => id switch
    {
        IdStandard => Standard100x80(),
        IdCompact => Compact60x40(),
        IdBilingual => Bilingual120x90(),
        _ => null,
    };

    public const string IdStandard = "builtin.standard-100x80";
    public const string IdCompact = "builtin.compact-60x40";
    public const string IdBilingual = "builtin.bilingual-120x90";

    /// <summary>标准外贸箱唛：上收货人、中合同/明细、下件号与原产地，带外框与分隔线。</summary>
    public static LabelTemplate Standard100x80()
    {
        var t = New(IdStandard, "标准箱唛 100×80", "外贸通用四周围框式，含分隔线；适合大面积不干胶", 100, 80);
        t.BorderMm = 0;   // 外框用 Rect 元素显式画，便于单独控制线宽
        t.CropMarkMm = 0;

        t.Elements.Add(Rect(1.5, 1.5, 97, 77, 0.5));

        t.Elements.Add(Text("{{Consignee}}", 5, 5, 90, 15, 12, bold: true, HorizontalAlign.Left));
        t.Elements.Add(Line(1.5, 23, 98.5, 23, 0.4));

        t.Elements.Add(Text("{{ContractNo}}", 5, 24.5, 90, 8, 11, bold: true, HorizontalAlign.Center));
        t.Elements.Add(Text("Item No.: {{ItemNo}}", 5, 33.5, 90, 6.5, 9, bold: false, HorizontalAlign.Center));
        t.Elements.Add(Line(1.5, 42, 98.5, 42, 0.4));

        t.Elements.Add(Text("PO: {{PoNumber}}", 5, 43.5, 43, 6, 9));
        t.Elements.Add(Text("Dest.: {{DestinationPort}}", 52, 43.5, 43, 6, 9));

        t.Elements.Add(Text("G.W.: {{GrossWeight}} KG", 5, 50, 43, 6, 9));
        t.Elements.Add(Text("N.W.: {{NetWeight}} KG", 52, 50, 43, 6, 9));

        t.Elements.Add(Text("Meas.: {{Measurement}} CBM", 5, 56.5, 43, 6, 9));
        t.Elements.Add(Text("Size: {{BoxSize}} CM", 52, 56.5, 43, 6, 9));

        t.Elements.Add(Line(1.5, 64, 98.5, 64, 0.4));
        t.Elements.Add(Text("C/NOS. {{NoXofY}}", 5, 65, 90, 8, 12, bold: true, HorizontalAlign.Center));
        t.Elements.Add(Text("{{Origin}}", 5, 73.5, 90, 4.5, 8, bold: false, HorizontalAlign.Center));

        return t;
    }

    /// <summary>精简小唛：只留最关键的四项，适合小规格不干胶或内箱。</summary>
    public static LabelTemplate Compact60x40()
    {
        var t = New(IdCompact, "精简小唛 60×40", "小规格不干胶：客户、合同号、目的港、件号", 60, 40);
        t.BorderMm = 0;

        t.Elements.Add(Rect(1, 1, 58, 38, 0.4));
        t.Elements.Add(Text("{{Consignee}}", 3, 2.5, 54, 8, 9, bold: true, HorizontalAlign.Left));
        t.Elements.Add(Text("{{ContractNo}}", 3, 11, 54, 6, 8, bold: false, HorizontalAlign.Center));
        t.Elements.Add(Line(1, 18.5, 59, 18.5, 0.3));
        t.Elements.Add(Text("{{DestinationPort}}", 3, 19.5, 54, 5, 7.5));
        t.Elements.Add(Text("C/NOS. {{NoXofY}}", 3, 25, 54, 6, 9.5, bold: true, HorizontalAlign.Center));
        t.Elements.Add(Text("{{Origin}}", 3, 31.5, 54, 4.5, 7, bold: false, HorizontalAlign.Center));

        return t;
    }

    /// <summary>中英对照全字段：一箱信息量大或多语言要求时用。</summary>
    public static LabelTemplate Bilingual120x90()
    {
        var t = New(IdBilingual, "中英对照全字段 120×90", "字段最全，中英双语标题；适合整柜大件唛", 120, 90);
        t.BorderMm = 0;

        t.Elements.Add(Rect(2, 2, 116, 86, 0.6));
        t.Elements.Add(Text("{{Consignee}}", 5, 3.5, 110, 8, 13, bold: true, HorizontalAlign.Center));
        t.Elements.Add(Line(2, 13, 118, 13, 0.4));

        var y = 14.5;
        const double rowH = 6;
        const double leftX = 5;
        const double leftW = 52;
        const double rightX = 60;
        const double rightW = 55;

        t.Elements.Add(Text("合同号 Contract: {{ContractNo}}", leftX, y, leftW, rowH, 8.5));
        t.Elements.Add(Text("PO 号: {{PoNumber}}", rightX, y, rightW, rowH, 8.5));
        y += 6.5;
        t.Elements.Add(Text("货号 Item: {{ItemNo}}", leftX, y, leftW, rowH, 8.5));
        t.Elements.Add(Text("件号 C/Nos: {{NoXofY}}", rightX, y, rightW, rowH, 9, bold: true));
        y += 6.5;
        t.Elements.Add(Text("毛重 G.W.: {{GrossWeight}} KG", leftX, y, leftW, rowH, 8.5));
        t.Elements.Add(Text("净重 N.W.: {{NetWeight}} KG", rightX, y, rightW, rowH, 8.5));
        y += 6.5;
        t.Elements.Add(Text("体积 Meas.: {{Measurement}} CBM", leftX, y, leftW, rowH, 8.5));
        t.Elements.Add(Text("箱规 Size: {{BoxSize}} CM", rightX, y, rightW, rowH, 8.5));
        y += 6.5;
        t.Elements.Add(Text("数量 QTY: {{Quantity}} PCS", leftX, y, leftW, rowH, 8.5));
        t.Elements.Add(Text("批次 Batch: {{BatchNo}}", rightX, y, rightW, rowH, 8.5));
        y += 6.5;
        t.Elements.Add(Text("目的港 P.O.D.: {{DestinationPort}}", leftX, y, leftW, rowH, 8.5));
        t.Elements.Add(Text("日期 Date: {{ShipDate}}", rightX, y, rightW, rowH, 8.5));
        y += 6.5;
        t.Elements.Add(Text("目的国 Country: {{DestinationCountry}}", leftX, y, leftW, rowH, 8.5));
        t.Elements.Add(Text("客户代码 C/S: {{ClientCode}}", rightX, y, rightW, rowH, 8.5));

        t.Elements.Add(Line(2, 61.5, 118, 61.5, 0.4));
        t.Elements.Add(Text("备注 Remarks: {{Remarks}}", 5, 62.5, 110, 8, 8));
        t.Elements.Add(Line(2, 72, 118, 72, 0.4));
        t.Elements.Add(Text("{{Origin}}", 5, 73, 110, 9, 12, bold: true, HorizontalAlign.Center));

        return t;
    }

    private static LabelTemplate New(string id, string name, string note, double w, double h) => new()
    {
        Id = id,
        Name = name,
        Note = note,
        WidthMm = w,
        HeightMm = h,
        BuiltIn = true,
        BorderMm = 0,
        PaddingMm = 3,
    };

    private static TemplateElement Text(
        string text, double x, double y, double w, double h, double fontPt,
        bool bold = false, HorizontalAlign align = HorizontalAlign.Left) => new()
    {
        Kind = ElementKind.Text,
        Text = text,
        X = x,
        Y = y,
        Width = w,
        Height = h,
        FontSizePt = fontPt,
        Bold = bold,
        Align = align,
        ShrinkToFit = true,
        MaxLines = 2,
    };

    private static TemplateElement Line(double x, double y, double x2, double y2, double thickness) => new()
    {
        Kind = ElementKind.Line,
        X = x,
        Y = y,
        X2 = x2,
        Y2 = y2,
        Width = Math.Abs(x2 - x),
        Height = Math.Abs(y2 - y),
        ThicknessMm = thickness,
    };

    private static TemplateElement Rect(double x, double y, double w, double h, double thickness) => new()
    {
        Kind = ElementKind.Rect,
        X = x,
        Y = y,
        Width = w,
        Height = h,
        ThicknessMm = thickness,
    };
}
