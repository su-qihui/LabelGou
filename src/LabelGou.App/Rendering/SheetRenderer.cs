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
    private static readonly Color CropColor = Color.FromRgb(0, 0, 0);
    private static readonly Color RegistrationColor = Color.FromRgb(0, 120, 200);
    private static readonly Color OutlineColor = Color.FromRgb(170, 170, 170);
    private static readonly Color PaperEdgeColor = Color.FromRgb(150, 150, 150);
    private static readonly Color MarginColor = Color.FromRgb(200, 200, 200);

    /// <summary>屏幕上再细的线也至少留 0.6 DIU，否则高 dpi 与低 dpi 看着不一样。</summary>
    private const double ScreenMinThicknessDiu = 0.6;

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
        if (includeTrimMarks)
        {
            foreach (var mark in ImpositionEngine.BuildMarks(plan.Spec, plan, page))
            {
                if (purpose != PageRenderPurpose.Screen && mark.Kind == SheetMarkKind.LabelOutline) continue;
                dc.DrawLine(PenFor(mark, scale, purpose),
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

            if (placement.Rotated)
                LabelRenderer.DrawRotated(dc, layout, scale, x, y, showElementGuides, pixelsPerDip);
            else
                LabelRenderer.Draw(dc, layout, scale, x, y, showElementGuides, pixelsPerDip);
        }

        if (purpose == PageRenderPurpose.Printer) dc.Pop();
    }

    /// <summary>
    /// 线宽口径：Core 给的 <see cref="SheetMarkLine.ThicknessMm"/> 是真实印刷线宽（默认 0.3mm 左右），
    /// 导出/打印必须按它换算，屏幕预览则保底可见。
    /// </summary>
    private static Pen PenFor(SheetMarkLine mark, double scale, PageRenderPurpose purpose)
    {
        var thickness = Mm.ToDiu(mark.ThicknessMm) * scale;
        if (purpose == PageRenderPurpose.Screen) thickness = Math.Max(ScreenMinThicknessDiu, thickness);
        thickness = Math.Max(0.2, thickness);

        var brush = new SolidColorBrush(mark.Kind switch
        {
            SheetMarkKind.RegistrationMark => RegistrationColor,
            SheetMarkKind.LabelOutline => OutlineColor,
            _ => CropColor,
        });
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        if (mark.Kind == SheetMarkKind.LabelOutline) pen.DashStyle = DashStyles.Dot;
        pen.Freeze();
        return pen;
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    /// <summary>整版自适应缩放（屏幕用）。</summary>
    public static double FitZoom(SheetPlan plan, double availableWidth, double availableHeight, double maxZoom = 4)
        => LabelRenderer.FitZoom(plan.PageWidthMm, plan.PageHeightMm, availableWidth, availableHeight, maxZoom);
}
