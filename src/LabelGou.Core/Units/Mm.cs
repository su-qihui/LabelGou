namespace LabelGou.Core.Units;

/// <summary>
/// 长度单位换算。全链路（模板坐标、排版落位、渲染、导出）统一以<strong>毫米</strong>为真源，
/// 只在绘制时按目标设备的 DPI 换算成设备单位，避免中途混用像素导致印刷尺寸漂移。
/// WPF 的设备无关单位（DIU）固定为 1/96 英寸，与显示器实际 DPI 无关。
/// </summary>
public static class Mm
{
    /// <summary>1 英寸 = 25.4 毫米。</summary>
    public const double MmPerInch = 25.4;

    /// <summary>WPF/96DPI 下每英寸的设备无关单位数。</summary>
    public const double DiuPerInch = 96.0;

    /// <summary>毫米 → WPF 设备无关单位（DIU）。</summary>
    public static double ToDiu(double millimeters) => millimeters / MmPerInch * DiuPerInch;

    /// <summary>WPF 设备无关单位 → 毫米。</summary>
    public static double FromDiu(double diu) => diu / DiuPerInch * MmPerInch;

    /// <summary>毫米 → 像素（按给定 DPI，用于图片/PDF 导出）。</summary>
    public static double ToPixels(double millimeters, double dpi) => millimeters / MmPerInch * dpi;

    /// <summary>磅（字号单位）→ 毫米。1 磅 = 1/72 英寸。</summary>
    public static double PointToMm(double points) => points / 72.0 * MmPerInch;

    /// <summary>毫米 → 磅。</summary>
    public static double MmToPoint(double millimeters) => millimeters * 72.0 / MmPerInch;

    /// <summary>把毫米格式化成对人友好的字符串（如 100 或 100.5）。</summary>
    public static string Format(double millimeters)
        => millimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " mm";
}
