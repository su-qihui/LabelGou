using LabelGou.Core.Templates;

namespace LabelGou.Core.Editing;

/// <summary>
/// 编辑器用的模板/元素工厂：负责「给一个能直接印的起点」，
/// 而不是让用户面对一个空画布不知道怎么下手。
/// </summary>
public static class TemplateFactory
{
    /// <summary>新建元素时的默认落点搜索步长（毫米）。</summary>
    public const double FreeSpotStepMm = 2;

    /// <summary>元素之间至少留出的呼吸间距（毫米），判重叠时用。</summary>
    public const double MinGapMm = 0.5;

    /// <summary>
    /// 空白但可用的起步模板：一个外框、一行客户名、一条分隔线、一行产地。
    /// 四样东西够用户马上明白“这些都能拖”，也不会因为空模板被校验警告。
    /// </summary>
    public static LabelTemplate Blank(string name, double widthMm = 100, double heightMm = 80)
    {
        var template = new LabelTemplate
        {
            Name = string.IsNullOrWhiteSpace(name) ? "我的模板" : name.Trim(),
            WidthMm = widthMm,
            HeightMm = heightMm,
            PaddingMm = 4,
            BorderMm = 0.5,
            BuiltIn = false,
        };

        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Rect,
            X = template.PaddingMm,
            Y = template.PaddingMm,
            Width = Math.Max(1, widthMm - 2 * template.PaddingMm),
            Height = Math.Max(1, heightMm - 2 * template.PaddingMm),
            ThicknessMm = 0.35,
        });
        template.Elements.Add(NewText("{{Consignee}}", template.PaddingMm + 2, template.PaddingMm + 2,
            Math.Max(10, widthMm - 2 * template.PaddingMm - 4), 8, fontPt: 11, bold: true));
        template.Elements.Add(NewLine(template.PaddingMm + 2, template.PaddingMm + 12,
            Math.Max(template.PaddingMm + 12, widthMm - template.PaddingMm - 2), template.PaddingMm + 12));
        template.Elements.Add(NewText("{{Origin}}", template.PaddingMm + 2, heightMm - template.PaddingMm - 9,
            Math.Max(10, widthMm / 2 - template.PaddingMm), 7, fontPt: 8));
        return template;
    }

    /// <summary>从内置模板起步做用户副本（内置模板不可覆盖，编辑器一律走这条路）。</summary>
    public static LabelTemplate CopyOf(LabelTemplate source, string newName)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var copy = source.CloneAsUserCopy(string.IsNullOrWhiteSpace(newName) ? source.Name + "（改）" : newName.Trim());
        copy.BuiltIn = false;
        return copy;
    }

    public static TemplateElement NewText(string content, double x, double y, double width, double height,
        double fontPt = 8, bool bold = false, HorizontalAlign align = HorizontalAlign.Left)
        => new()
        {
            Kind = ElementKind.Text,
            Text = content,
            X = x,
            Y = y,
            Width = Math.Max(EditGeometry.MinSideMm, width),
            Height = Math.Max(EditGeometry.MinSideMm, height),
            FontSizePt = fontPt,
            Bold = bold,
            Align = align,
        };

    public static TemplateElement NewLine(double x1, double y1, double x2, double y2, double thicknessMm = 0.35)
        => new()
        {
            Kind = ElementKind.Line,
            X = x1,
            Y = y1,
            X2 = x2,
            Y2 = y2,
            ThicknessMm = thicknessMm,
            // Line 不用 Width/Height，但保持正数免得校验器以外的地方拿到 0 值
            Width = Math.Abs(x2 - x1),
            Height = Math.Abs(y2 - y1),
        };

    public static TemplateElement NewRect(double x, double y, double width, double height, double thicknessMm = 0.35)
        => new()
        {
            Kind = ElementKind.Rect,
            X = x,
            Y = y,
            Width = Math.Max(EditGeometry.MinSideMm, width),
            Height = Math.Max(EditGeometry.MinSideMm, height),
            ThicknessMm = thicknessMm,
        };

    public static TemplateElement NewImage(string absoluteOrRelativePath, double x, double y, double width, double height)
        => new()
        {
            Kind = ElementKind.Image,
            ImagePath = absoluteOrRelativePath,
            X = x,
            Y = y,
            Width = Math.Max(EditGeometry.MinSideMm, width),
            Height = Math.Max(EditGeometry.MinSideMm, height),
        };

    /// <summary>
    /// 加一个元素：<strong>默认位置与已有元素重叠时自动往下找空位</strong>，
    /// 免得新建的东西正好压在客户名上、用户以为没加上。找不到空位就返回 null（模板已满）。
    /// </summary>
    public static int? AddElement(LabelTemplate template, TemplateElement element, double xPreferred, double yPreferred)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (template.Elements.Count >= TemplateValidator.MaxElements) return null;

        var spot = FindFreeSpot(template, element, xPreferred, yPreferred);
        if (spot is null) return null;

        element.X = Math.Max(0, Math.Min(spot.Value.X, Math.Max(0, template.WidthMm - EditGeometry.BoxOf(element).Width)));
        element.Y = Math.Max(0, Math.Min(spot.Value.Y, Math.Max(0, template.HeightMm - EditGeometry.BoxOf(element).Height)));
        if (element.Kind == ElementKind.Line)
        {
            var lengthX = element.X2 - element.X;
            var lengthY = element.Y2 - element.Y;
            element.X2 = element.X + lengthX;
            element.Y2 = element.Y + lengthY;
        }

        template.Elements.Add(element);
        return template.Elements.Count - 1;
    }

    /// <summary>加一个绑定字段的文本元素（编辑器「插入字段」按钮走这里）。</summary>
    public static int? AddFieldText(LabelTemplate template, string token, double widthMm = 40, double heightMm = 6, double fontPt = 8)
    {
        var field = $"{{{{{token}}}}}";
        return AddElement(template, NewText(field, template.PaddingMm, template.PaddingMm, widthMm, heightMm, fontPt),
            template.PaddingMm, template.PaddingMm);
    }

    /// <summary>复制元素（含属性），偏移若干毫米，不加入列表。</summary>
    public static TemplateElement Duplicate(LabelTemplate template, int index, double offsetMm = 2)
    {
        if (index < 0 || index >= template.Elements.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"元素下标 {index} 超出范围。");
        var copy = template.Elements[index].CloneTemplate();
        copy.X += offsetMm;
        copy.Y += offsetMm;
        if (copy.Kind == ElementKind.Line)
        {
            copy.X2 += offsetMm;
            copy.Y2 += offsetMm;
        }
        return copy;
    }

    /// <summary>
    /// 从偏好位置开始找第一个不与任何可见元素重叠的落点（按离目标位置由近到远扫），
    /// 找不到（纸面已被占满）返回 null。
    /// </summary>
    public static (double X, double Y)? FindFreeSpot(LabelTemplate template, TemplateElement element, double preferredX, double preferredY)
    {
        var box = EditGeometry.BoxOf(element);
        var maxX = template.WidthMm - box.Width;
        var maxY = template.HeightMm - box.Height;
        if (maxX < 0 || maxY < 0) return (0, 0);   // 元素比标签还大：交给校验器报错，这里先给个位置

        var step = Math.Max(0.5, FreeSpotStepMm);
        var xs = Grid(0, maxX, step, preferredX);
        var ys = Grid(0, maxY, step, preferredY);

        foreach (var y in ys)
        {
            foreach (var x in xs)
            {
                if (!OverlapsAny(template, element, x, y)) return (x, y);
            }
        }
        return null;
    }

    /// <summary>候选坐标序列，按“离偏好值近的先试”排序。</summary>
    private static List<double> Grid(double from, double to, double step, double preferred)
    {
        var list = new List<double>();
        for (var v = from; v <= to + 1e-6; v += step) list.Add(Math.Round(v, 3));
        if (list.Count == 0) list.Add(from);
        list.Sort((a, b) =>
        {
            var d = Math.Abs(a - preferred).CompareTo(Math.Abs(b - preferred));
            return d != 0 ? d : a.CompareTo(b);
        });
        return list;
    }

    /// <summary>两个毫米包围盒是否重叠（含 <see cref="MinGapMm"/> 呼吸间距）。</summary>
    public static bool Overlaps((double X, double Y, double Width, double Height) a, (double X, double Y, double Width, double Height) b, double gapMm = MinGapMm)
        => a.X < b.X + b.Width + gapMm && b.X < a.X + a.Width + gapMm
        && a.Y < b.Y + b.Height + gapMm && b.Y < a.Y + a.Height + gapMm;

    private static bool OverlapsAny(LabelTemplate template, TemplateElement element, double x, double y)
    {
        var box = EditGeometry.BoxOf(element);
        var probe = (x, y, box.Width, box.Height);
        foreach (var other in template.Elements)
        {
            if (!other.Visible) continue;
            if (Overlaps(probe, EditGeometry.BoxOf(other))) return true;
        }
        return false;
    }
}
