using System.Windows;
using System.Windows.Media;
using LabelGou.Core.Impos;
using LabelGou.Core.Layout;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>
/// 画一整页的三个去处，区别只在"哪些线是给活人看的、哪些是要印到纸上的"。
/// </summary>
public enum PageRenderPurpose
{
    /// <summary>屏幕预览：白纸边、页边虚线都画，像素密度按屏幕。</summary>
    Screen,
    /// <summary>位图/PDF/TIFF 导出：只画白底与真实内容，不画屏幕辅助线。</summary>
    Image,
    /// <summary>送打印机：连白底都不铺（省墨，白纸本色即可），只画要印的东西。</summary>
    Printer,
}

/// <summary>
/// 整版渲染的唯一出口：预览控件、位图导出、PDF、打印全部走这里。
/// <para>
/// 之所以抽出来，是为了守住 §五-22 那条约束——**不允许出现第二套落位画法**。
/// 缩放倍数 <paramref name="scale"/> 与 <see cref="Mm.ToDiu"/> 相乘就是设备无关单位坐标，
/// 导出时 scale = dpi/96、打印时 scale = 1（1:1 实物尺寸），几何关系与预览完全一致。
/// </para>
/// </summary>
public static class SheetRenderer
{
    private static readonly Color PaperEdgeColor = Color.FromRgb(150, 150, 150);
    private static readonly Color MarginColor = Color.FromRgb(200, 200, 200);

    /// <summary>页用途 → 线宽口径项（只有屏幕需要保底，其余一律真实毫米）。</summary>
    public static RenderTarget TargetFor(PageRenderPurpose purpose) => purpose switch
    {
        PageRenderPurpose.Screen => RenderTarget.Screen,
        PageRenderPurpose.Printer => RenderTarget.Printer,
        _ => RenderTarget.Bitmap,
    };

    public static void DrawPage(
        DrawingContext dc,
        SheetPlan plan,
        int pageIndex,
        double scale,
        Func<int, LabelLayout?>? layoutProvider,
        bool showElementGuides,
        PageRenderPurpose purpose,
        double pixelsPerDip,
        bool includeTrimMarks = true)
    {
        var page = Math.Max(1, pageIndex);
        var paperW = Mm.ToDiu(plan.PageWidthMm) * scale;
        var paperH = Mm.ToDiu(plan.PageHeightMm) * scale;
        var paperRect = new Rect(0, 0, paperW, paperH);

        if (purpose == PageRenderPurpose.Printer)
        {
            // 裁剪到纸张范围：驱动给的可视区偶尔比纸大一点，多画的线会蹭到下一张。
            dc.PushClip(new RectangleGeometry(paperRect));
        }
        else if (purpose == PageRenderPurpose.Screen)
        {
            dc.DrawRectangle(Brushes.White, Frozen(new Pen(new SolidColorBrush(PaperEdgeColor), Math.Max(1, scale))), paperRect);
        }
        else
        {
            dc.DrawRectangle(Brushes.White, null, paperRect);
        }

        if (purpose == PageRenderPurpose.Screen)
        {
            var marginRect = new Rect(
                Mm.ToDiu(plan.Spec.MarginLeftMm) * scale,
                Mm.ToDiu(plan.Spec.MarginTopMm) * scale,
                Math.Max(0, Mm.ToDiu(plan.Spec.UsableWidthMm) * scale),
                Math.Max(0, Mm.ToDiu(plan.Spec.UsableHeightMm) * scale));
            dc.DrawRectangle(null, Frozen(new Pen(new SolidColorBrush(MarginColor), 0.6) { DashStyle = DashStyles.Dash }), marginRect);
        }

        // 辅助线先画，让标签内容压在上面（印刷上角线本来就只露在标签外）
        var target = TargetFor(purpose);
        if (includeTrimMarks)
        {
            foreach (var mark in ImpositionEngine.BuildMarks(plan.Spec, plan, page))
            {
                if (purpose != PageRenderPurpose.Screen && mark.Kind == SheetMarkKind.LabelOutline) continue;
                dc.DrawLine(PenFor(mark, scale, target),
                    new Point(Mm.ToDiu(mark.X1) * scale, Mm.ToDiu(mark.Y1) * scale),
                    new Point(Mm.ToDiu(mark.X2) * scale, Mm.ToDiu(mark.Y2) * scale));
            }
        }

        if (layoutProvider is null)
        {
            if (purpose == PageRenderPurpose.Printer) dc.Pop();
            return;
        }

        foreach (var placement in plan.PlacementsOnPage(page))
        {
            var layout = layoutProvider(placement.LabelIndex);
            if (layout is null) continue;

            var x = Mm.ToDiu(placement.X) * scale;
            var y = Mm.ToDiu(placement.Y) * scale;

            // 整版里不给每枚标签铺白底、也不描那圈 0.26mm 灰框：
            // 那个参数就是为整版场景设的（单枚预览才用）。一铺就会盖掉同一页上的套准十字与角线，
            // 而那圈灰框在打印/PDF/PNG 上都是多出来的一道脏线（SVG 出口又没有，五出口就漂移了）。
            if (placement.Rotated)
                LabelRenderer.DrawRotated(dc, layout, scale, x, y, showElementGuides, pixelsPerDip,
                    drawBackground: false, target: target);
            else
                LabelRenderer.Draw(dc, layout, scale, x, y, showElementGuides, pixelsPerDip,
                    drawBackground: false, target: target);
        }

        if (purpose == PageRenderPurpose.Printer) dc.Pop();
    }

    /// <summary>
    /// 标记画笔：线宽走 <see cref="RenderRules"/> 的唯一口径，颜色也取同一份（与 SVG 出口同源）。
    /// <para>Core 给的 <see cref="SheetMarkLine.ThicknessMm"/> 是真实印刷线宽（默认 0.3mm 左右），
    /// 导出/打印按它换算，屏幕预览保底可见。</para>
    /// <para><strong>预览里角线与套准十字一律虚线</strong>（2026-09-08 用户口径「打印出来一般不需要标线，
    /// 不过预览可以以虚线显示」）：这些线是给人看位置的，画成实线会让人以为纸上真有这道墨。
    /// 打印/PDF/位图/SVG 出口一律还是实线——要上纸的必须是实的，所以判据是 <paramref name="target"/>
    /// 而不是线的类型。</para>
    /// </summary>
    private static Pen PenFor(SheetMarkLine mark, double scale, RenderTarget target)
    {
        var brush = new SolidColorBrush(mark.Kind switch
        {
            SheetMarkKind.RegistrationMark => RenderRules.RegistrationColor,
            SheetMarkKind.LabelOutline => RenderRules.LabelOutlineColor,
            _ => RenderRules.CropMarkColor,
        });
        brush.Freeze();
        return RenderRules.PenFor(brush, mark.ThicknessMm, scale, target, DashStyleFor(mark.Kind, target));
    }

    /// <summary>
    /// 这条线在这个去处该用什么样式：虚线 = 给眼睛看的示意，实线 = 真要上纸的墨。
    /// <para>单独抽出一个口子是因为「预览虚线、打印实线」这件事只体现在 WPF 画笔上，
    /// 没得单测就等于没规矩（与第 11 棒那条「只有眼睛看得到的错要补拓扑钉子」同一路做法）。</para>
    /// </summary>
    public static DashStyle? DashStyleFor(SheetMarkKind kind, RenderTarget target) => kind switch
    {
        SheetMarkKind.LabelOutline => DashStyles.Dot,          // 刀模示意线：本来就是点线
        _ => target == RenderTarget.Screen ? DashStyles.Dash : null,
    };

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    /// <summary>整版自适应缩放（屏幕用）。</summary>
    public static double FitZoom(SheetPlan plan, double availableWidth, double availableHeight, double maxZoom = 4)
        => LabelRenderer.FitZoom(plan.PageWidthMm, plan.PageHeightMm, availableWidth, availableHeight, maxZoom);
}
