namespace LabelGou.Core.Templates;

/// <summary>
/// A 类内置标准唛头模板。
/// <para>
/// <strong>2026-09-09 重列：清单只留「跟店里那四档开法配得上」的那几份。</strong>
/// 用户原话「<strong>现在的内置模版几乎全都不符逻辑（而且一般用的是一开四：28*20 的 14*10 的 4 个标签，
/// 一开八 28*20 的 10*7 的 8 个，大开二 28*20 的 20*14 的 2 个，小开二 16*24 的 16*12 的 2 个）</strong>」。
/// 所以下拉里的名字直接拿开法打头（③ 步选模板与 ④ 步选纸规能一眼对上），版式一律是真样张那一套四行：
/// 顶部客户名一大条 + Item no / QTY pcs / Ctns 件 三行明细。
/// </para>
/// <para>
/// M1 那三套「框线分格」式（标准箱唛 / 精简小唛 / 中英对照）一家真样张都对不上，已退出下拉；
/// 方法本身留着（<c>GetById</c> 仍可解析，老方案与单测还要用），其中九字段那套留着当
/// 「自动挑模板按命中率而不是按命中数」那条回归测试的对手（它引用 9 个字段，行式四行只引用 4 个）。
/// </para>
/// </summary>
public static class BuiltInTemplates
{
    /// <summary>全部内置模板（按店里常用的四档开法排在前）。</summary>
    public static IReadOnlyList<LabelTemplate> All { get; } = new[]
    {
        RowsFour140x100(),      // 一开四・28×20 纸上 4 枚
        RowsFour100x70(),       // 一开八・28×20 纸上 8 枚
        RowsFour200x140(),      // 大开二・28×20 纸上 2 枚
        RowsFour160x120(),      // 小开二・16×24 纸上 2 枚
        RowsBigTwo160x120(),
        Standard100x80(),
        // 邱总 / OLU / TOP 三套故意不列在这里：用户给的那批样张是「让 AI 去学的训练素材」，
        // 不是让我抄成五个内置选项。它们的定义留在本文件里当评测基准（见下面三个方法的注）。
        // 精简小唛 60×40 与中英对照 120×90 也退出了清单：labelgou-CL 里五家没一家是这个形状。
    };

    /// <summary>每次调用返回新实例，避免界面编辑时污染内置定义。</summary>
    public static LabelTemplate? GetById(string? id) => id switch
    {
        IdStandard => Standard100x80(),
        IdCompact => Compact60x40(),
        IdBilingual => Bilingual120x90(),
        IdRowsFour => RowsFour140x100(),
        IdRowsEight => RowsFour100x70(),
        IdRowsBig2Four => RowsFour200x140(),
        IdRowsSmall2Four => RowsFour160x120(),
        IdRowsBigTwo => RowsBigTwo160x120(),
        _ => null,
    };

    public const string IdStandard = "builtin.standard-100x80";
    public const string IdCompact = "builtin.compact-60x40";
    public const string IdBilingual = "builtin.bilingual-120x90";
    public const string IdRowsFour = "builtin.rows-140x100-4line";
    public const string IdRowsBigTwo = "builtin.rows-160x120-2line";

    /// <summary>一开八配套：100×70 四行。</summary>
    public const string IdRowsEight = "builtin.rows-100x70-4line";

    /// <summary>大开二配套：200×140 四行。</summary>
    public const string IdRowsBig2Four = "builtin.rows-200x140-4line";

    /// <summary>小开二配套：160×120 四行。</summary>
    public const string IdRowsSmall2Four = "builtin.rows-160x120-4line";

    /// <summary>以下三个 id 不在内置清单里（不进下拉），只服务 AI 认版式的评测基准。</summary>
    public const string IdQiuRows = "builtin.qiu-140x100-4line";

    /// <summary>OLU：160×120 首行纯货号大字 + 三行小字（无冒号）。</summary>
    public const string IdOluRows = "builtin.olu-160x120-4line";

    /// <summary>TOP：140×100 四行左对齐（半角冒号+空格、常规字重）。</summary>
    public const string IdTopRows = "builtin.top-140x100-4line";

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

    /// <summary>
    /// 行式四行 140×100（无框）：客户名撑满一大条 + 三行明细。<strong>一开四配套那一档</strong>。
    /// <para>形状直接抄自 <c>labelgou-CL\7.8 金沐 唛头</c> 的 CDR 真样张与导出 SVG
    /// （BOLAROM / Item no：… / QTY：… pcs / Ctns：…件）。客户名走 <c>{{Consignee}}</c>，
    /// 不写死厂牌——同一个模板要能给下一家厂用。件数那一行走 <c>{{col:本行箱数}}</c> 而不是
    /// <c>{{CartonTotal}}</c>：展开后后者是整批总数（155），厂商要的是本货号的 5 件。</para>
    /// <para>三行明细的 41.4pt 是从真件 SVG 量出来的（字高 14.602mm）。其余三档开法拿同一套四行，
    /// 只是按各自的行带高同比缩字号（见 <see cref="FourRows"/>）。</para>
    /// </summary>
    public static LabelTemplate RowsFour140x100() => FourRows(
        IdRowsFour, "一开四・四行 140×100（金沐样张）",
        "抄自 7.8 金沐 唛头 CDR+SVG：顶部客户名一大条居中 + 货号/数量/件数三行同字号明细左对齐，无框线。配 28×20 一开四那张纸。",
        140, 100, paddingMm: 4, gapMm: 1.5, detailPt: 41.4);

    /// <summary>行式四行 100×70：<strong>一开八配套</strong>（28×20 纸上一张 8 枚）。</summary>
    public static LabelTemplate RowsFour100x70() => FourRows(
        IdRowsEight, "一开八・四行 100×70",
        "与一开四同一套四行，只是按 100×70 的行带同比缩了字号。配 28×20 一开八那张纸（一张 8 枚）。",
        100, 70, paddingMm: 3, gapMm: 1.2, detailPt: 28.6);

    /// <summary>行式四行 200×140：<strong>大开二配套</strong>（28×20 纸上一张 2 枚）。</summary>
    public static LabelTemplate RowsFour200x140() => FourRows(
        IdRowsBig2Four, "大开二・四行 200×140",
        "与一开四同一套四行，行带更宽所以字号更大。配 28×20 大开二那张纸（一张 2 枚，大箱唛）。",
        200, 140, paddingMm: 6, gapMm: 2, detailPt: 57.7);

    /// <summary>行式四行 160×120：<strong>小开二配套</strong>（16×24 纸上一张 2 枚）。</summary>
    public static LabelTemplate RowsFour160x120() => FourRows(
        IdRowsSmall2Four, "小开二・四行 160×120",
        "与一开四同一套四行。配 16×24 小开二那张纸（一张 2 枚，小箱/内箱唛）。",
        160, 120, paddingMm: 5, gapMm: 1.8, detailPt: 49.5);

    /// <summary>
    /// 四行骨架的唯一实现：顶部客户名撑满（权重 1.4）+ 三行同字号明细。
    /// <para><paramref name="detailPt"/> 怎么来的：金沐真件量到「明细行带 19.9mm ↔ 41.4pt」，
    /// 即每毫米行带约 2.08pt；其余三档拿同一个比例算出自己的行带高再乘上它。
    /// 为什么写定数而不是让三行也 Stretch：真件上三行是同一个字号，而 Stretch 会按各自内容长度
    /// 反算，哪一行值长一点就被缩字，于是 QTY/Ctns 反而比 Item no 大——那就是用户圈过的「字体大小不统一」。</para>
    /// </summary>
    private static LabelTemplate FourRows(string id, string name, string note,
        double widthMm, double heightMm, double paddingMm, double gapMm, double detailPt)
    {
        var spec = new RowLayoutSpec
        {
            Id = id,
            Name = name,
            Note = note,
            WidthMm = widthMm,
            HeightMm = heightMm,
            PaddingMm = paddingMm,
            GapMm = gapMm,
            DrawBorder = false,
        };
        spec.Rows.Add(new RowSpec { Content = "{{Consignee}}", Weight = 1.4, Stretch = true, Align = HorizontalAlign.Center });
        spec.Rows.Add(new RowSpec { Content = "Item no：{{ItemNo}}", Weight = 1, SizePt = detailPt });
        spec.Rows.Add(new RowSpec { Content = "QTY：{{Quantity}} pcs", Weight = 1, SizePt = detailPt });
        spec.Rows.Add(new RowSpec { Content = "Ctns：{{col:本行箱数}}件", Weight = 1, SizePt = detailPt });
        return MarkBuiltIn(spec);
    }

    /// <summary>
    /// 邱总 140×100 四行全居中（全角冒号）。抄自 <c>labelgou-CL\邱总</c> 真样张。
    /// <para><strong>不进内置清单</strong>：那是把训练素材当答案抄给用户选。本方法现在的身份是
    /// 「AI 认版式」的评测基准——模型看这张样张就该输出版式 JSON，跟这份定义对得上才算会。</para>
    /// <para>真件第二行是 <c>ITEM：香水 perfume</c>（那批货的品名，不随箱变），但它是那一单的货，
    /// 给别的表用就会多印一行不相干的字（用户圈出的“乱加一个不知道什么”）——所以品名必须走字段而不是写死。</para>
    /// </summary>
    public static LabelTemplate QiuRows140x100()
    {
        var spec = new RowLayoutSpec
        {
            Id = IdQiuRows,
            Name = "邱总・四行居中 140×100",
            Note = "抄自 邱总 CDR：JP / ITEM：香水 perfume / ITEM No：… / QTY：… PCS，四行全居中、全角冒号，无框无图。",
            WidthMm = 140,
            HeightMm = 100,
            PaddingMm = 4,
            GapMm = 1.5,
            DrawBorder = false,
        };
        spec.Rows.Add(new RowSpec { Content = "{{Consignee}}", Weight = 1, SizePt = 59.5, Align = HorizontalAlign.Center });
        spec.Rows.Add(new RowSpec { Content = "ITEM：香水 perfume", Weight = 1, SizePt = 45, Align = HorizontalAlign.Center });
        spec.Rows.Add(new RowSpec { Content = "ITEM No：{{ItemNo}}", Weight = 1, SizePt = 45, Align = HorizontalAlign.Center });
        spec.Rows.Add(new RowSpec { Content = "QTY：{{Quantity}} PCS", Weight = 1, SizePt = 53, Align = HorizontalAlign.Center });
        return MarkBuiltIn(spec);
    }

    /// <summary>
    /// OLU 160×120：首行是**无标签的纯货号大字**（真件里占 85% 行宽），后三行左对齐小字。
    /// <para>抄自 <c>labelgou-CL\OLU</c>。已知欠账：真件右侧还有两个运输标志（带框易碎 + 向上箭头），
    /// 本工具还没有矢量标志库，这两块暂不画；真件把货号大写化（olu830-70→OLU830-70）也还没做。</para>
    /// </summary>
    public static LabelTemplate OluRows160x120()
    {
        var spec = new RowLayoutSpec
        {
            Id = IdOluRows,
            Name = "OLU・大字货号 160×120",
            Note = "抄自 OLU CDR：首行纯货号放到最大 + 三行小字（数量PCS / 固定行 / MADE IN CHINA）。右侧两个运输标志待矢量库补。",
            WidthMm = 160,
            HeightMm = 120,
            PaddingMm = 6,
            GapMm = 2,
            DrawBorder = false,
        };
        spec.Rows.Add(new RowSpec { Content = "{{ItemNo}}", Weight = 1.6, Stretch = true });
        const double smallPt = 23.5;
        spec.Rows.Add(new RowSpec { Content = "{{Quantity}}PCS", Weight = 1, SizePt = smallPt });
        spec.Rows.Add(new RowSpec { Content = "Z&X", Weight = 1, SizePt = smallPt });
        spec.Rows.Add(new RowSpec { Content = "MADE IN CHINA", Weight = 1, SizePt = smallPt });
        return MarkBuiltIn(spec);
    }

    /// <summary>
    /// TOP 140×100 四行左对齐：中英双标签 + 半角冒号后跟一个空格 + 常规字重（五家里字最细）。
    /// <para>抄自 <c>labelgou-CL\TOP</c>。已知欠账三件：右上 EAN-13 条码（条码引擎未做）、
    /// 左上黑底反白 TOP 块（需图片槽）、品名行 <c>品名DESC: …</c>（字段目录里还没有“品名”这一项）。</para>
    /// </summary>
    public static LabelTemplate TopRows140x100()
    {
        var spec = new RowLayoutSpec
        {
            Id = IdTopRows,
            Name = "TOP・双语文四行 140×100",
            Note = "抄自 TOP CDR：货号/装箱数/件数/MADE IN CHINA 四行，半角冒号+空格、常规字重。条码与 TOP 标志块、品名行待补。",
            WidthMm = 140,
            HeightMm = 100,
            PaddingMm = 5,
            GapMm = 1.5,
            DrawBorder = false,
        };
        const double topPt = 24;
        spec.Rows.Add(new RowSpec { Content = "货号ITEM NO: {{ItemNo}}", Weight = 1, SizePt = topPt, Bold = false });
        spec.Rows.Add(new RowSpec { Content = "装箱数QTY: {{Quantity}} PCS", Weight = 1, SizePt = topPt, Bold = false });
        spec.Rows.Add(new RowSpec { Content = "件数CTN: {{col:本行箱数}} 件", Weight = 1, SizePt = topPt, Bold = false });
        spec.Rows.Add(new RowSpec { Content = "MADE IN CHINA", Weight = 1, SizePt = topPt, Bold = false });
        return MarkBuiltIn(spec);
    }

    /// <summary>
    /// 大字两行 160×120（无框、居中）：整张纸只放两行超大字。
    /// <para>抄自 <c>labelgou-CL\广州郑小姐唛头流水</c>（<c>QI YUE:</c> / <c>AJ7-QI YUE: Aj9</c>）
    /// 与 <c>邱总</c>（居中大字四行）那一类：字越大越好看，细枝字段一律不上纸。</para>
    /// </summary>
    public static LabelTemplate RowsBigTwo160x120()
    {
        var spec = new RowLayoutSpec
        {
            Id = IdRowsBigTwo,
            Name = "大字两行 160×120",
            Note = "整张只放两行超大字（居中撑满）：流水/唛头大字版，适合 160×120 的纸。",
            WidthMm = 160,
            HeightMm = 120,
            PaddingMm = 6,
            GapMm = 3,
            DrawBorder = false,
        };
        spec.Rows.Add(new RowSpec { Content = "{{Consignee}}", Stretch = true, Align = HorizontalAlign.Center });
        spec.Rows.Add(new RowSpec { Content = "{{ItemNo}}", Stretch = true, Align = HorizontalAlign.Center });
        return MarkBuiltIn(spec);
    }

    /// <summary>行式骨架算完补上内置标记（行高/字号由 <see cref="RowLayoutSpec"/> 算，不手标毫米）。</summary>
    private static LabelTemplate MarkBuiltIn(RowLayoutSpec spec)
    {
        var template = spec.Build()
            ?? throw new InvalidOperationException($"行式骨架 {spec.Name} 排不出来：留白/行距把版面吃光了。");
        template.BuiltIn = true;
        return template;
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
