namespace LabelGou.Core.Impos;

/// <summary>
/// 内置纸规种子：只覆盖打印店最常见的那几张底纸，其余让用户复制后自己改。
/// <para>
/// 刻意不抄 gLabels 的上千条纸规库（本项目只在自家店用，几百条反而没人看得懂）。
/// 真实刀模规格拿到后直接在这里补，或让用户在界面里另存（见 <see cref="SheetSpecStore"/>）。
/// </para>
/// <para>
/// <strong>每个方法都返回新实例</strong>：纸规会被界面拿去改数值，共用同一份对象会把内置定义改掉。
/// </para>
/// </summary>
public static class BuiltInSheetSpecs
{
    public const string IdA4 = "sheet.a4";

    public const string IdA4Landscape = "sheet.a4.landscape";

    public const string IdA3 = "sheet.a3";

    public const string IdA4Dense = "sheet.a4.dense";

    public const string IdA4Small = "sheet.a4.small-60x40";

    /// <summary>一页一枚：纸 = 唛头 + 页边，1 列 1 行。<strong>默认档</strong>：五家真样张全是这种用法。</summary>
    public const string IdOnePerLabel = "sheet.one-per-label";

    /// <summary>
    /// 28×20 的纸上一并排 4 枚：这就是厂商表里那个「一开四」的档。
    /// <para>用户 2026-09-08 拿红框定案：「<strong>开四就是一张排 4 个一模一样的</strong>」，
    /// 所以选这档时一枚唛头独占一页、页内铺满 4 份全同（见 <see cref="SheetSpec.RepeatSameLabelPerPage"/>）。
    /// 上一版曾推断「一开四只是纸张裁切与贴法指令（金沐 D3 批注原文「张数等于件数」）、一张纸只印一枚」，
    /// 那个结论已被否掉（同一家的那句批注现在由 ④ 步「按哪一列数张数」直接按列取）。</para>
    /// </summary>
    public const string IdCut4_280x200 = "sheet.cut4-280x200";

    /// <summary>28×20 纸上一开八：单枚 100×70，一张 8 枚（用户 2026-09-09 给的四档开法之一）。</summary>
    public const string IdCut8_280x200 = "sheet.cut8-280x200";

    /// <summary>28×20 纸上大开二：单枚 200×140，一张 2 枚。</summary>
    public const string IdBig2_280x200 = "sheet.big2-280x200";

    /// <summary>16×24 纸上小开二：单枚 160×120，一张 2 枚（竖着叠两枚，不用旋转）。</summary>
    public const string IdSmall2_160x240 = "sheet.small2-160x240";

    public const string IdCustom = "sheet.custom";

    /// <summary>全部内置纸规（每次调用都是新实例）。四档开法排在最前：那是店里天天用的那几张。</summary>
    public static IReadOnlyList<SheetSpec> All() => new List<SheetSpec>
    {
        Cut4_280x200(), Cut8_280x200(), Big2_280x200(), Small2_160x240(),
        OnePerLabel(), A4(), A4Landscape(), A3(), A4Dense(), A4Small60x40(), Custom(),
    };

    /// <summary>按 id 取一份新副本；未知 id 返回 null。</summary>
    public static SheetSpec? GetById(string? id) => id switch
    {
        IdA4 => A4(),
        IdA4Landscape => A4Landscape(),
        IdA3 => A3(),
        IdA4Dense => A4Dense(),
        IdA4Small => A4Small60x40(),
        IdCut4_280x200 => Cut4_280x200(),
        IdCut8_280x200 => Cut8_280x200(),
        IdBig2_280x200 => Big2_280x200(),
        IdSmall2_160x240 => Small2_160x240(),
        IdOnePerLabel => OnePerLabel(),
        IdCustom => Custom(),
        _ => null,
    };

    /// <summary>A4 底纸：唛头最常用，标签尺寸跟随模板，自动密排。</summary>
    public static SheetSpec A4() => New(IdA4, "A4 底纸",
        "210×297，四边 8 mm，自动密排，允许旋转省料。标签尺寸跟随所选模板。", 210, 297, 8, 2);

    /// <summary>A4 横向：宽幅唛头（120×90 双语版之类）常能多放一枚。</summary>
    public static SheetSpec A4Landscape()
    {
        var spec = New(IdA4Landscape, "A4 横向",
            "同样的 A4 纸横过来用，宽幅唛头（如 120×90 双语版）常能多放一枚。", 210, 297, 8, 2);
        spec.Landscape = true;
        return spec;
    }

    /// <summary>A3 底纸：整批大箱唛省纸首选。</summary>
    public static SheetSpec A3() => New(IdA3, "A3 底纸",
        "297×420，整批大箱唛省纸首选。", 297, 420, 10, 3);

    /// <summary>A4 密排：边距与间距都压到最小，枚数最大化。</summary>
    public static SheetSpec A4Dense() => New(IdA4Dense, "A4 密排省料",
        "边距 5 mm、间距 1 mm，枚数最大化；裁切留白较少，适合只裁一刀的情况。", 210, 297, 5, 1);

    /// <summary>A4 上 60×40 小标，固定 3 列 × 6 行 = 18 枚（与已购刀模纸对齐用）。</summary>
    public static SheetSpec A4Small60x40()
    {
        var spec = New(IdA4Small, "A4 小标 60×40（3 列 × 6 行）",
            "固定网格：小唛头/侧唛贴纸，一页 18 枚。行列写死便于与已购刀模纸对齐。", 210, 297, 8, 3);
        spec.LabelWidthMm = 60;
        spec.LabelHeightMm = 40;
        spec.Columns = 3;
        spec.Rows = 6;
        spec.AllowRotate = false;
        return spec;
    }

    /// <summary>自定义起点：复制后按店里刀模实测数值改。</summary>
    public static SheetSpec Custom() => New(IdCustom, "自定义（另存后改）",
        "空白起点：把纸张、页边、间距、行列改成你店里刀模的实际数值。", 210, 297, 10, 2);

    /// <summary>
    /// 一页一枚（纸面跟随当前模板尺寸）：默认档。
    /// <para>占位尺寸按 140×100 的唛头 + 2mm 页边给（给校验器一个合法的数），引擎会在
    /// <c>ImpositionEngine.Build</c> 里按真模板重算，所以模板换成 160×120 也仍是一页一枚。</para>
    /// </summary>
    public static SheetSpec OnePerLabel()
    {
        var spec = New(IdOnePerLabel, "一页一枚（纸 = 唛头）",
            "每张纸只印一枚唛头，纸面按当前模板尺寸自动展开（含页边）。厂商 CDR 页面本身就是单枚尺寸，所以这一档常要；要「一张纸排 4 个一模一样」就选 28×20 一开四那档。", 144, 104, 2, 0);
        spec.FollowsLabel = true;
        return spec;
    }

    /// <summary>
    /// 280×200 上一纸四枚：2 列 × 2 行铺满，每枚 140×100，中间那一刀就是两枚标签的公共边。
    /// <para>用户 2026-09-08 拿红框定案：<strong>「开四就是一张排 4 个一模一样的」</strong> —— 所以选这档时
    /// 一枚唛头独占一页、页内铺满 4 份全同（<see cref="SheetSpec.RepeatSameLabelPerPage"/>）；
    /// 要一页只一枚就换 <see cref="OnePerLabel"/>。页边与间距都是 0：
    /// 尺寸是 4×(140×100) 刚好铺满，多留 1mm 就排不下 4 枚。</para>
    /// <para>页边为 0 的代价：角线与套准十字在纸上没有空边带可放，引擎会整角不画并给出告警（不是默默画一半）。
    /// 要裁切参考线就得把页边留到 <c>CropMarkGapMm + CropMarkLengthMm</c> 以上，那时每页枚数会随之下降。</para>
    /// </summary>
    public static SheetSpec Cut4_280x200()
    {
        var spec = New(IdCut4_280x200, "一开四・28×20 纸 140×100（2 × 2 = 4 枚）",
            "整张铺满 4 枚 140×100，页边与间距 0；铺满就没有地方画角线与套准十字（会提示你），中间那一刀沿两枚标签的公共边。", 280, 200, 0, 0);
        spec.LabelWidthMm = 140;
        spec.LabelHeightMm = 100;
        spec.Columns = 2;
        spec.Rows = 2;
        spec.AllowRotate = false;
        spec.CropMarks = CropMarkMode.SheetCorners;
        return spec;
    }

    /// <summary>
    /// 280×200 纸上一开八：单枚 100×70。用户 2026-09-09 定案的四档开法之一：
    /// 「<strong>一开八 28*20 的 10*7 的 8 个标签</strong>」。
    /// <para>几何上只有一种排法能拿到 8 枚：正着放（100 横 70 竖）只能 2×2 = 4 枚，
    /// 转 90°（70 横 100 竖）才能 4×2 = 8 枚，而 8×7000 mm² = 56000 mm² = 整张纸面积，一分不剩。
    /// 所以这一档必须开着「允许旋转」：标签在纸面上是横过来的，但裁下来贴到箱子上时字是正的
    /// （旋转的是摆位，不是内容）。行列故意不写死（=0 自动密排），交给引擎自己算出 4×2。</para>
    /// </summary>
    public static SheetSpec Cut8_280x200()
    {
        var spec = New(IdCut8_280x200, "一开八・28×20 纸 100×70（转 90° 摆满 8 枚）",
            "整张铺满 8 枚 100×70（4 列 × 2 行，标签转 90° 摆位；裁下来贴箱上字是正的），页边与间距 0。", 280, 200, 0, 0);
        spec.LabelWidthMm = 100;
        spec.LabelHeightMm = 70;
        spec.AllowRotate = true;
        spec.CropMarks = CropMarkMode.SheetCorners;
        return spec;
    }

    /// <summary>
    /// 280×200 纸上大开二：单枚 200×140（「<strong>大开二 28*20 的 20*14 的 2 标签</strong>」）。
    /// <para>跟一开八同一个道理：200×140 正着放一张只能塞 1 枚，转 90°（140 横 200 竖）才能左右两枚
    /// （2×140 = 280 整宽，200 整高），所以这一档也开着允许旋转。</para>
    /// </summary>
    public static SheetSpec Big2_280x200()
    {
        var spec = New(IdBig2_280x200, "大开二・28×20 纸 200×140（转 90° 摆 2 枚）",
            "整张铺满 2 枚 200×140（转 90° 后左右各一枚，刚好铺满），页边与间距 0；大箱唛用。", 280, 200, 0, 0);
        spec.LabelWidthMm = 200;
        spec.LabelHeightMm = 140;
        spec.AllowRotate = true;
        spec.CropMarks = CropMarkMode.SheetCorners;
        return spec;
    }

    /// <summary>
    /// 160×240 纸上小开二：单枚 160×120（「<strong>小开二 16*24 的 16*12 的 2 个标签</strong>」）。
    /// <para>这一档不用旋转：纸宽 160 = 标签宽 160，纸高 240 = 两枚 120 竖着叠，1×2 刚好铺满。</para>
    /// </summary>
    public static SheetSpec Small2_160x240()
    {
        var spec = New(IdSmall2_160x240, "小开二・16×24 纸 160×120（1 列 × 2 行 = 2 枚）",
            "整张铺满 2 枚 160×120（竖着叠两枚，不旋转），页边与间距 0；小箱/内箱唛用。", 160, 240, 0, 0);
        spec.LabelWidthMm = 160;
        spec.LabelHeightMm = 120;
        spec.AllowRotate = false;
        spec.CropMarks = CropMarkMode.SheetCorners;
        return spec;
    }

    private static SheetSpec New(string id, string name, string note,
        double paperW, double paperH, double margin, double gutter) => new()
    {
        Id = id,
        Name = name,
        Note = note,
        BuiltIn = true,
        PaperWidthMm = paperW,
        PaperHeightMm = paperH,
        MarginLeftMm = margin,
        MarginTopMm = margin,
        MarginRightMm = margin,
        MarginBottomMm = margin,
        GutterXMm = gutter,
        GutterYMm = gutter,
        AllowRotate = true,
        CropMarks = CropMarkMode.SheetCorners,
        RegistrationMarks = true,
    };
}
