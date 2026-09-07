using LabelGou.Core.Templates;

namespace LabelGou.Core.Impos;

/// <summary>
/// 单页网格方案（一版能摆几枚、按哪个朝向）。
/// </summary>
/// <param name="Columns">每行几枚。</param>
/// <param name="Rows">每页几行。</param>
/// <param name="PerPage">每页枚数。</param>
/// <param name="LabelWidthMm">落位后标签在纸面上的实际宽（旋转时已是交换过的值）。</param>
/// <param name="LabelHeightMm">实际高。</param>
/// <param name="Rotated">是否采用了旋转 90° 的省料方案。</param>
/// <param name="UsedWidthMm">网格整体占宽（毫米）。</param>
/// <param name="UsedHeightMm">网格整体占高（毫米）。</param>
public sealed record GridPlan(
    int Columns,
    int Rows,
    int PerPage,
    double LabelWidthMm,
    double LabelHeightMm,
    bool Rotated,
    double UsedWidthMm,
    double UsedHeightMm);

/// <summary>
/// 一枚标签在整版上的落位（毫米，原点在<strong>纸张左上角</strong>，Y 向下，与模板坐标同一套约定）。
/// </summary>
/// <param name="LabelIndex">第几张标签（1 起，对应标签记录集的下标 + 1）。</param>
/// <param name="PageIndex">第几页（1 起）。</param>
/// <param name="Row">页内行号（0 起）。</param>
/// <param name="Column">页内列号（0 起）。</param>
/// <param name="X">左上角 X。</param>
/// <param name="Y">左上角 Y。</param>
/// <param name="Width">标签实际宽（旋转后）。</param>
/// <param name="Height">标签实际高（旋转后）。</param>
/// <param name="Rotated">本枚是否旋转 90°。</param>
public sealed record LabelPlacement(
    int LabelIndex,
    int PageIndex,
    int Row,
    int Column,
    double X,
    double Y,
    double Width,
    double Height,
    bool Rotated);

public enum SheetMarkKind
{
    /// <summary>裁切角线。</summary>
    CropMark = 0,

    /// <summary>套准十字（分条/上机对位用）。</summary>
    RegistrationMark = 1,

    /// <summary>刀模示意线。</summary>
    LabelOutline = 2,
}

/// <summary>整版上的一条辅助线（毫米）。</summary>
public sealed record SheetMarkLine(SheetMarkKind Kind, double X1, double Y1, double X2, double Y2, double ThicknessMm);

/// <summary>
/// 一次拼版的完整结果（纯数据，渲染/PDF/图片导出共用）。
/// </summary>
public sealed class SheetPlan
{
    public required SheetSpec Spec { get; init; }

    public required GridPlan Grid { get; init; }

    /// <summary>模板原始标签宽（未旋转）。</summary>
    public double TemplateLabelWidthMm { get; init; }

    /// <summary>模板原始标签高。</summary>
    public double TemplateLabelHeightMm { get; init; }

    /// <summary>本次要出的标签总数。</summary>
    public int LabelCount { get; init; }

    public IReadOnlyList<LabelPlacement> Placements { get; init; } = Array.Empty<LabelPlacement>();

    public IReadOnlyList<TemplateIssue> Issues { get; init; } = Array.Empty<TemplateIssue>();

    /// <summary>另一种朝向每页能放几枚（0 表示纸规不允许旋转）。</summary>
    public int AlternativePerPage { get; init; }

    /// <summary>另一种朝向是横还是竖（true 表示"反过来放"是旋转方案）。</summary>
    public bool AlternativeRotated { get; init; }

    public int PerPage => Grid.PerPage;

    public int PageCount => PerPage <= 0 ? 0 : (int)Math.Ceiling(LabelCount / (double)PerPage);

    public int LabelsLastPage => PerPage <= 0 || LabelCount == 0
        ? 0
        : LabelCount - (PageCount - 1) * PerPage;

    /// <summary>末页空出来的枚数位（省料评估要看它）。</summary>
    public int EmptySlotsLastPage => PerPage <= 0 ? 0 : PerPage - LabelsLastPage;

    /// <summary>纸张利用率（%）：标签总面积 ÷ 实际耗用纸张面积，含末页空位。</summary>
    public double UtilizationPercent
    {
        get
        {
            var paper = Spec.PaperAreaMm2 * Math.Max(1, PageCount);
            if (paper <= 0) return 0;
            var used = LabelCount * TemplateLabelWidthMm * TemplateLabelHeightMm;
            return Math.Round(used / paper * 100, 1);
        }
    }

    /// <summary>整版宽（毫米），渲染时按它算缩放。</summary>
    public double PageWidthMm => Spec.EffectivePaperWidthMm;

    public double PageHeightMm => Spec.EffectivePaperHeightMm;

    public IReadOnlyList<LabelPlacement> PlacementsOnPage(int pageIndex)
        => Placements.Where(p => p.PageIndex == pageIndex).ToList();

    /// <summary>纸规/拼版里的 Error 条数。出口前的闸门要用它（以前 <c>HasError()</c> 没一处消费 <c>plan.Issues</c>）。</summary>
    public int ErrorCount => Issues.Count(i => i.Severity == IssueLevel.Error);

    /// <summary>
    /// 页号越界检查（页号统一 0 起始）。
    /// <para>打印与导出共用同一句文案：上一版只有导出端查、打印端没查，选错页会静默少打。</para>
    /// </summary>
    public void CollectPageRangeIssues(IReadOnlyList<int>? pageIndexes, IList<string> issues)
    {
        foreach (var index in pageIndexes ?? Array.Empty<int>())
        {
            if (index < 0 || index >= Math.Max(1, PageCount))
            {
                issues.Add($"页序号 {index + 1} 超出整版页数（共 {PageCount} 页）。");
                break;
            }
        }
    }

    /// <summary>一句话总结，直接给状态栏用。</summary>
    public string Describe()
    {
        if (PerPage <= 0) return "当前纸规放不下任何标签，请检查页边与标签尺寸";
        var rotate = Grid.Rotated ? "（已旋转省料）" : string.Empty;
        var spare = AlternativePerPage > PerPage && AlternativeRotated
            ? $"（若允许旋转可每页 {AlternativePerPage} 枚）"
            : string.Empty;
        return $"{Grid.Columns} 列 × {Grid.Rows} 行 = 每页 {PerPage} 枚{rotate}{spare} · " +
               $"{LabelCount} 张标签需 {PageCount} 页（末页 {LabelsLastPage} 枚）· 用纸利用率 {UtilizationPercent:0.#}%";
    }
}

/// <summary>
/// 拼版引擎：把「标签尺寸 + 数量 + 纸规」变成整版落位与裁切/套准线。
/// <para>
/// 这里是 M7「AI 出方案、引擎保精度」里<strong>保精度</strong>的那一半：
/// AI 或用户只能改纸规与模板，几何一律由本引擎算，绝不允许外部直接指定毫米坐标。
/// 所有输出都是毫米纯数据，WPF 预览、PDF 导出、整版图片导出共用，不加任何渲染依赖。
/// </para>
/// </summary>
public static class ImpositionEngine
{
    /// <summary>浮点比较容差（毫米）。</summary>
    private const double Eps = 1e-6;

    /// <summary>
    /// 生成整版方案。
    /// </summary>
    /// <param name="spec">纸规。</param>
    /// <param name="labelWidthMm">标签宽（模板尺寸；纸规自带尺寸时由调用方换算好再传）。</param>
    /// <param name="labelHeightMm">标签高。</param>
    /// <param name="labelCount">要出的标签数。</param>
    public static SheetPlan Build(SheetSpec spec, double labelWidthMm, double labelHeightMm, int labelCount)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // 「一页一枚」在这里把纸面展开成具体毫米：必须在校验与排版之前、而且只在这一处。
        // 预览/打印/PDF 三条出口都从 Build 走，在界面里再算一份就会算出两套页码。
        // 传进来的 spec 永远是调用方新取的一份副本（BuiltInSheetSpecs.GetById / SheetSpecStore.ListAll），
        // 所以就地改写不会把下拉清单里的那一项尺寸带跑。
        if (spec.FollowsLabel && labelWidthMm > Eps && labelHeightMm > Eps)
        {
            // 页边四个值是贴在「有效框」的两个轴上的（UsableWidthMm = EffectivePaperWidth - 左右边距），
            // 横向用纸时这两个轴对调了：有效框的宽来自 PaperHeightMm，不是 PaperWidthMm。
            // 上一版不区分 Landscape 就直接把标签宽加左右边距写进 PaperWidthMm，于是非对称页边时
            // 算出的纸面跟标签轴向正好错开 90°，每页 0 枚 —— 预览与打印出一张白纸。
            var across = labelWidthMm + spec.MarginLeftMm + spec.MarginRightMm;
            var down = labelHeightMm + spec.MarginTopMm + spec.MarginBottomMm;
            if (spec.Landscape)
            {
                spec.PaperHeightMm = across;
                spec.PaperWidthMm = down;
            }
            else
            {
                spec.PaperWidthMm = across;
                spec.PaperHeightMm = down;
            }
        }

        var issues = new List<TemplateIssue>(SheetSpecValidator.Validate(spec, labelWidthMm, labelHeightMm));
        var (w, h) = EffectiveLabelSize(spec, labelWidthMm, labelHeightMm);

        if (w <= Eps || h <= Eps)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error, "标签尺寸无效（模板或纸规的宽高为 0）。"));
            return new SheetPlan { Spec = spec, Grid = new GridPlan(0, 0, 0, w, h, false, 0, 0), Issues = issues };
        }

        var straight = FitGrid(spec, w, h, rotated: false);
        var rotatedFit = FitGrid(spec, h, w, rotated: true);

        GridPlan grid;
        GridPlan other;
        if (spec.AllowRotate && rotatedFit.PerPage > straight.PerPage)
        {
            grid = rotatedFit;
            other = straight;
            issues.Add(new TemplateIssue(IssueLevel.Info,
                $"旋转 90° 后每页可多放 {rotatedFit.PerPage - straight.PerPage} 枚，已自动采用旋转排布。"));
        }
        else
        {
            grid = straight;
            other = rotatedFit;
            if (!spec.AllowRotate && rotatedFit.PerPage > straight.PerPage)
            {
                issues.Add(new TemplateIssue(IssueLevel.Info,
                    $"打开纸规的「允许旋转」后每页可多放 {rotatedFit.PerPage - straight.PerPage} 枚，更省纸。"));
            }
        }

        if (spec.Columns > 0 && grid.Columns < spec.Columns)
        {
            // 固定列数被上面悄悄收敛了，必须告知，否则用户以为按自己写的排的
            issues.Add(new TemplateIssue(IssueLevel.Error,
                $"指定的 {spec.Columns} 列放不下（可用宽度最多放 {MaxCols(spec, grid)} 列），已按 {grid.Columns} 列落位。"));
        }
        if (spec.Rows > 0 && grid.Rows < spec.Rows)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error,
                $"指定的 {spec.Rows} 行放不下（可用高度最多 {MaxRows(spec, grid)} 行），已按 {grid.Rows} 行落位。"));
        }

        if (grid.PerPage <= 0)
        {
            issues.Add(new TemplateIssue(IssueLevel.Error,
                $"这张纸（可用区 {spec.UsableWidthMm:0.#} × {spec.UsableHeightMm:0.#} mm）放不下 " +
                $"{w:0.#} × {h:0.#} mm 的标签，请减小页边/间距或换大纸。"));
            return new SheetPlan
            {
                Spec = spec,
                Grid = grid,
                TemplateLabelWidthMm = labelWidthMm,
                TemplateLabelHeightMm = labelHeightMm,
                LabelCount = Math.Max(0, labelCount),
                Issues = issues,
            };
        }

        var placements = Place(grid, spec, labelCount);

        var plan = new SheetPlan
        {
            Spec = spec,
            Grid = grid,
            TemplateLabelWidthMm = labelWidthMm,
            TemplateLabelHeightMm = labelHeightMm,
            LabelCount = Math.Max(0, labelCount),
            Placements = placements,
            AlternativePerPage = other.PerPage,
            AlternativeRotated = other.Rotated,
            Issues = issues,
        };

        // 辅助线画不画得出来，取决于第 1 页的空白边带够不够（每页几何一样，不必逐页报）。
        // 这一步必须排在 plan 算好之后，因为要拿真实落位当遮挡物来判；
        // 报在这里而不是让它静默发生在渲染端，是为了让界面那列 issue 能解释「为什么纸上没有十字」。
        foreach (var note in BuildMarkNotes(spec, plan, 1))
            issues.Add(new TemplateIssue(IssueLevel.Warning, note));

        return plan;
    }

    /// <summary>
    /// 这一页的套准十字为什么没画全。<see cref="BuildMarks"/> 与 <see cref="Build"/> 共用同一份判据，
    /// 避免「引擎少画了线、界面却说没事」两套说法。
    /// <para>角线的余量问题不在这里报：它不依赖本页落位（页边与角线参数就够了），由
    /// <see cref="SheetSpecValidator.Validate"/> 一次报清，省得同一条问题在 issue 列表里出现两次。</para>
    /// </summary>
    private static IEnumerable<string> BuildMarkNotes(SheetSpec spec, SheetPlan plan, int pageIndex)
    {
        if (!spec.RegistrationMarks) yield break;

        var placements = plan.PlacementsOnPage(pageIndex);
        if (placements.Count == 0) yield break;

        var covered = CountCoveredCorners(spec, placements);
        if (covered == 0) yield break;

        var paperW = spec.EffectivePaperWidthMm;
        var paperH = spec.EffectivePaperHeightMm;
        var band = Math.Min(
            Math.Min(placements.Min(p => p.X), paperW - placements.Max(p => p.X + p.Width)),
            Math.Min(placements.Min(p => p.Y), paperH - placements.Max(p => p.Y + p.Height)));
        yield return $"有 {covered} 个角的套准十字位置被标签占着（十字要 " +
            $"{spec.RegistrationInsetMm + spec.RegistrationSizeMm / 2:0.#} mm 的空白边带，纸面只比标签多 {band:0.#} mm），这几个角没画 —— 套准标记不该落在印面上。";
    }

    /// <summary>纸规自带标签尺寸时以纸规为准（它代表刀模，物理尺寸不可违），否则跟随模板。</summary>
    public static (double Width, double Height) EffectiveLabelSize(SheetSpec spec, double templateWidthMm, double templateHeightMm)
        => spec.FollowTemplateSize
            ? (templateWidthMm, templateHeightMm)
            : (spec.LabelWidthMm, spec.LabelHeightMm);

    /// <summary>
    /// 给定一个朝向，算出网格。行列填了固定值就尊重它，但放不下时自动收敛为可放数量并交由调用方提示。
    /// </summary>
    private static GridPlan FitGrid(SheetSpec spec, double labelWidthMm, double labelHeightMm, bool rotated)
    {
        var autoCols = Count(UsableFit(spec.UsableWidthMm, labelWidthMm, spec.GutterXMm));
        var autoRows = Count(UsableFit(spec.UsableHeightMm, labelHeightMm, spec.GutterYMm));

        var cols = spec.Columns > 0 ? Math.Min(spec.Columns, autoCols) : autoCols;
        var rows = spec.Rows > 0 ? Math.Min(spec.Rows, autoRows) : autoRows;

        var usedW = cols <= 0 ? 0 : cols * labelWidthMm + (cols - 1) * spec.GutterXMm;
        var usedH = rows <= 0 ? 0 : rows * labelHeightMm + (rows - 1) * spec.GutterYMm;

        return new GridPlan(cols, rows, cols * rows, labelWidthMm, labelHeightMm, rotated, usedW, usedH);
    }

    /// <summary>固定列数被收敛时，用于把“最多能放几列”写进提示。</summary>
    private static int MaxCols(SheetSpec spec, GridPlan grid)
        => Count(UsableFit(spec.UsableWidthMm, grid.LabelWidthMm, spec.GutterXMm));

    private static int MaxRows(SheetSpec spec, GridPlan grid)
        => Count(UsableFit(spec.UsableHeightMm, grid.LabelHeightMm, spec.GutterYMm));

    /// <summary>可用长度里能塞几个 length（含间距）。结果可能是 0。</summary>
    private static int UsableFit(double usable, double length, double gutter)
    {
        if (usable <= Eps || length <= Eps) return 0;
        if (gutter <= Eps) return (int)Math.Floor(usable / length + Eps);
        return (int)Math.Floor((usable + gutter) / (length + gutter) + Eps);
    }

    private static int Count(int value) => Math.Max(0, value);

    /// <summary>行优先铺满每一页（先右后下，操作员翻页方向与读书一致）。</summary>
    private static List<LabelPlacement> Place(GridPlan grid, SheetSpec spec, int labelCount)
    {
        var list = new List<LabelPlacement>(Math.Max(0, labelCount));
        if (grid.PerPage <= 0 || labelCount <= 0) return list;

        for (var i = 0; i < labelCount; i++)
        {
            var page = i / grid.PerPage;
            var slot = i % grid.PerPage;
            var row = slot / grid.Columns;
            var col = slot % grid.Columns;

            var x = spec.MarginLeftMm + col * (grid.LabelWidthMm + spec.GutterXMm);
            var y = spec.MarginTopMm + row * (grid.LabelHeightMm + spec.GutterYMm);

            list.Add(new LabelPlacement(
                LabelIndex: i + 1,
                PageIndex: page + 1,
                Row: row,
                Column: col,
                X: Math.Round(x, 4),
                Y: Math.Round(y, 4),
                Width: grid.LabelWidthMm,
                Height: grid.LabelHeightMm,
                Rotated: grid.Rotated));
        }
        return list;
    }

    /// <summary>
    /// 某一页上的裁切线与套准标记（毫米，纸张坐标）。
    /// 按页临时生成而不是一次算完：一份几百页的任务没必要把每页的线都留在内存里。
    /// </summary>
    public static IReadOnlyList<SheetMarkLine> BuildMarks(SheetSpec spec, SheetPlan plan, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(plan);

        var marks = new List<SheetMarkLine>();
        var placements = plan.PlacementsOnPage(pageIndex);
        if (placements.Count == 0) return marks;

        var paperW = spec.EffectivePaperWidthMm;
        var paperH = spec.EffectivePaperHeightMm;

        if (spec.LabelOutlineMm > 0)
        {
            foreach (var p in placements)
            {
                marks.Add(new SheetMarkLine(SheetMarkKind.LabelOutline, p.X, p.Y, p.X + p.Width, p.Y, spec.LabelOutlineMm));
                marks.Add(new SheetMarkLine(SheetMarkKind.LabelOutline, p.X + p.Width, p.Y, p.X + p.Width, p.Y + p.Height, spec.LabelOutlineMm));
                marks.Add(new SheetMarkLine(SheetMarkKind.LabelOutline, p.X + p.Width, p.Y + p.Height, p.X, p.Y + p.Height, spec.LabelOutlineMm));
                marks.Add(new SheetMarkLine(SheetMarkKind.LabelOutline, p.X, p.Y + p.Height, p.X, p.Y, spec.LabelOutlineMm));
            }
        }

        switch (spec.CropMarks)
        {
            case CropMarkMode.SheetCorners:
            {
                // 外接矩形按本页实际内容算：末页只有一半标签时，裁切线跟着缩，不会裁到空处
                double minX = placements.Min(p => p.X), maxX = placements.Max(p => p.X + p.Width);
                double minY = placements.Min(p => p.Y), maxY = placements.Max(p => p.Y + p.Height);
                AddCornerMarks(marks, minX, minY, maxX, maxY, spec,
                    CornerBudget(minX, minY, maxX, maxY, paperW, paperH, spec.CropMarkGapMm));
                break;
            }
            case CropMarkMode.EveryLabel:
                foreach (var p in placements)
                {
                    // 相邻两枚各自向外画，能用的宽度只对半算（校验器已对间距不足给过告警）
                    var limit = Math.Min(
                        CornerBudget(p.X, p.Y, p.X + p.Width, p.Y + p.Height, paperW, paperH, spec.CropMarkGapMm),
                        Math.Min(spec.GutterXMm, spec.GutterYMm) / 2 - spec.CropMarkGapMm);
                    AddCornerMarks(marks, p.X, p.Y, p.X + p.Width, p.Y + p.Height, spec, limit);
                }
                break;
        }

        if (spec.RegistrationMarks)
        {
            var inset = spec.RegistrationInsetMm;
            var size = spec.RegistrationSizeMm;
            var thickness = Math.Max(0.1, spec.CropMarkThicknessMm);
            var corners = new[]
            {
                (inset + size / 2, inset + size / 2),
                (paperW - inset - size / 2, inset + size / 2),
                (inset + size / 2, paperH - inset - size / 2),
                (paperW - inset - size / 2, paperH - inset - size / 2),
            };
            foreach (var (cx, cy) in corners)
            {
                // 十字是给操作员对纸用的，落在标签印面上只会污染画面（而且下一层白底一铺就看不见了）：
                // 哪个角被标签占着就整角不画，原因由 BuildMarkNotes 进 issue 列表。
                if (CoveredByLabel(placements, cx - size / 2, cy - size / 2, cx + size / 2, cy + size / 2)) continue;
                marks.Add(new SheetMarkLine(SheetMarkKind.RegistrationMark, cx - size / 2, cy, cx + size / 2, cy, thickness));
                marks.Add(new SheetMarkLine(SheetMarkKind.RegistrationMark, cx, cy - size / 2, cx, cy + size / 2, thickness));
            }
        }

        return marks;
    }

    /// <summary>一段角线短到这个数以下就没意义了（1mm 在打印店那张纸上只是一粒墨）。</summary>
    private const double MinCornerSegmentMm = 1.5;

    /// <summary>十字/角线这一段是否被某一枚标签的矩形占住（留 0.05mm 容差避开共边误判）。</summary>
    private static bool CoveredByLabel(IReadOnlyList<LabelPlacement> placements, double left, double top, double right, double bottom)
    {
        const double pad = 0.05;
        return placements.Any(p =>
            left < p.X + p.Width - pad && right > p.X + pad &&
            top < p.Y + p.Height - pad && bottom > p.Y + pad);
    }

    /// <summary>四个角里有多少个套准十字没地方放。</summary>
    private static int CountCoveredCorners(SheetSpec spec, IReadOnlyList<LabelPlacement> placements)
    {
        var paperW = spec.EffectivePaperWidthMm;
        var paperH = spec.EffectivePaperHeightMm;
        var half = spec.RegistrationSizeMm / 2;
        var inset = spec.RegistrationInsetMm;
        var xLeft = inset + half;
        var xRight = paperW - inset - half;
        var yTop = inset + half;
        var yBottom = paperH - inset - half;
        return new[] { (xLeft, yTop), (xRight, yTop), (xLeft, yBottom), (xRight, yBottom) }
            .Count(c => CoveredByLabel(placements, c.Item1 - half, c.Item2 - half, c.Item1 + half, c.Item2 + half));
    }

    /// <summary>
    /// 整版四角角线能用的最长一段：四边里最窄的那条空白带（已经扣过与标签边缘的间隙）。
    /// <para>这个值是主动收短 <c>len</c> 的依据，不再靠 <see cref="Clamp"/> 把线夹成看不见的尾巴。</para>
    /// </summary>
    private static double CornerBudget(double left, double top, double right, double bottom,
        double paperW, double paperH, double gap)
        => Math.Max(0, Math.Min(Math.Min(left - gap, paperW - right - gap),
            Math.Min(top - gap, paperH - bottom - gap)));

    /// <summary>给一个矩形区域补四条角线（每角两段，全部向外）。</summary>
    /// <param name="lenLimit">单段可用上限（毫米）：由外缘空白带算出，不够长就主动收短而不是被纸张边界夹掉。</param>
    private static void AddCornerMarks(List<SheetMarkLine> marks, double left, double top, double right, double bottom,
        SheetSpec spec, double lenLimit)
    {
        if (spec.CropMarkLengthMm <= 0) return;

        var len = Math.Min(spec.CropMarkLengthMm, lenLimit);
        if (len < MinCornerSegmentMm) return;
        var gap = spec.CropMarkGapMm;
        var thickness = spec.CropMarkThicknessMm <= 0 ? 0.15 : spec.CropMarkThicknessMm;
        var paperW = spec.EffectivePaperWidthMm;
        var paperH = spec.EffectivePaperHeightMm;

        void H(double x1, double y, double x2)
        {
            var a = Clamp(x1, 0, paperW);
            var b = Clamp(x2, 0, paperW);
            if (Math.Abs(a - b) > Eps && y >= 0 && y <= paperH)
                marks.Add(new SheetMarkLine(SheetMarkKind.CropMark, a, y, b, y, thickness));
        }

        void V(double x, double y1, double y2)
        {
            var a = Clamp(y1, 0, paperH);
            var b = Clamp(y2, 0, paperH);
            if (Math.Abs(a - b) > Eps && x >= 0 && x <= paperW)
                marks.Add(new SheetMarkLine(SheetMarkKind.CropMark, x, a, x, b, thickness));
        }

        // 左上
        H(left - gap - len, top, left - gap);
        V(left, top - gap - len, top - gap);
        // 右上
        H(right + gap, top, right + gap + len);
        V(right, top - gap - len, top - gap);
        // 左下
        H(left - gap - len, bottom, left - gap);
        V(left, bottom + gap, bottom + gap + len);
        // 右下
        H(right + gap, bottom, right + gap + len);
        V(right, bottom + gap, bottom + gap + len);
    }

    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    /// <summary>
    /// 省料对照：把同一批标签放到另一张纸规上会怎样（供界面做「换纸规看看省多少」的对比）。
    /// </summary>
    public static int ComparePages(SheetPlan a, SheetPlan b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.LabelCount != b.LabelCount)
            throw new ArgumentException("两份方案的标签数不同，无法直接比较");
        return b.PageCount - a.PageCount;   // >0 表示 a 更省纸
    }
}
