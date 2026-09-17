using LabelGou.Core.Editing;
using LabelGou.Core.Layout;
using LabelGou.Core.Units;

namespace LabelGou.App.Rendering;

/// <summary>
/// 一条文字<strong>看得见的那一块</strong>（毫米）——唯一量法。
/// <para>第 44 棒在编辑器里量的那份墨迹盒，第 46 棒起有两个消费方：编辑器的选中框/句柄/摆位钳制，
/// 以及"墨迹探出纸边没有"这道检查（编辑器按样例、出纸前按真数据）。两处必须同一次量法，
/// 否则会出现"框看着在纸内、闸门却说出去了"（§五-62 那一族）。</para>
/// <para>Core 量不了字（不许碰 WPF），所以这份量具留在 App 层；它只调生产画法用的
/// <see cref="TextFit"/>，不自建第二套排版。</para>
/// </summary>
public static class TextInkBox
{
    /// <summary>量一条版面项的墨迹盒（未旋转，已含字面拉伸）。量不到（零面积等）返回 null，不猜。</summary>
    public static (double X, double Y, double Width, double Height)? Measure(TextItem item)
    {
        var fit = TextFit.Solve(item, scale: 1.0, TextFit.CanonicalPixelsPerDip);
        if (fit is null) return null;

        // 墨迹本身只有一份算法（TextFit.InkDiu：真字形外接，含对齐偏移）——这里只叠字面拉伸。
        // 从前这一格自己拿 FormattedText 的行盒宽拼：行盒比字形高出一截，且折行那条路的对齐偏移
        // 根本没算进来，于是"框比字大、还往左挪"（第 81 棒，用户圈图那条）。
        var (ix, iy, iw, ih) = fit.InkDiu;
        var mx = Mm.FromDiu(ix);
        var my = Mm.FromDiu(iy);
        var mw = Math.Max(EditGeometry.MinSideMm, Mm.FromDiu(iw));
        var mh = Math.Max(EditGeometry.MinSideMm, Mm.FromDiu(ih));

        // 字面拉伸是"绕排版盒中心抻"，与 PushGeometry / 旋转锚点严格同一个中心。
        var cx = item.X + item.Width / 2;
        var cy = item.Y + item.Height / 2;
        return (
            cx + (mx - cx) * item.TextScaleX,
            cy + (my - cy) * item.TextScaleY,
            mw * item.TextScaleX,
            mh * item.TextScaleY);
    }

    /// <summary>墨迹转完之后的外接矩形（纸面毫米）。没转就等于墨迹盒本身。
    /// 旋转中心＝<strong>墨迹盒自己的中心</strong>（第 52 棒改口径：从前绕排版盒中心，与渲染端一起偏）。</summary>
    public static (double X, double Y, double Right, double Bottom) RotatedOf(TextItem item,
        (double X, double Y, double Width, double Height) ink)
        => EditGeometry.RotatedBoundsOf(ink, item.RotationDeg, ink.X + ink.Width / 2, ink.Y + ink.Height / 2);

    /// <summary>
    /// 这一版版面里<strong>文字墨迹探出纸边</strong>最多几毫米（含旋转后的外接、含字面拉伸）。
    /// <para>0 = 都在纸内。第 46 棒：文本改成"永不折行"之后，那条隐形行带不再兜住"内容别出纸"，
    /// 接手这份保护的就是这里——所以它既给编辑器的样例检查用，也给出纸前的真数据检查用。</para>
    /// </summary>
    public static double OverflowMm(LabelLayout layout)
    {
        var worst = 0d;
        foreach (var item in layout.Items.OfType<TextItem>())
        {
            var ink = Measure(item);
            if (ink is null) continue;
            var occ = RotatedOf(item, ink.Value);
            worst = Math.Max(worst, Math.Max(
                Math.Max(occ.Right - layout.WidthMm, occ.Bottom - layout.HeightMm),
                Math.Max(-occ.X, -occ.Y)));
        }
        return worst;
    }
}
