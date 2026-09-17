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
    /// 空白但可用的起步模板：一行客户名、一条分隔线、一行产地，外加一只<strong>可删的</strong>矩形框。
    /// 四样东西够用户马上明白“这些都能拖”，也不会因为空模板被校验警告。
    /// <para>第 81 棒⑥：<strong>外框线从 0.5 改成 0</strong>。用户报的是"新建模板时边框被固定了一个黑线边框
    /// 无法删除，导致打印时也被印出来"——那条 <see cref="LabelTemplate.BorderMm"/> 外框<strong>不是元素</strong>，
    /// 图层列表里根本没有它，他把四个元素全删了它还在那儿印（他机器上那份
    /// <c>我的唛头模板.json</c> 就是 <c>elems=0 / borderMm=0.5</c> 的现场）。想要一条框，模板里那只
    /// 矩形就是可删可拖的版本；真要纸边那条，工具栏「外框线」填个数就有。</para>
    /// <para><see cref="LabelTemplate.BorderMm"/> 的<strong>类默认值 0.5 故意不动</strong>：那是"想带一条外框"
    /// 的旧写法在用的值，改它等于动出纸行为（§五：默认值一动就要他点头）。</para>
    /// </summary>
    public static LabelTemplate Blank(string name, double widthMm = 100, double heightMm = 80)
    {
        var template = new LabelTemplate
        {
            Name = string.IsNullOrWhiteSpace(name) ? "我的模板" : name.Trim(),
            WidthMm = widthMm,
            HeightMm = heightMm,
            PaddingMm = 4,
            BorderMm = 0,
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
    /// 加一个元素：<strong>默认位置与已有元素重叠时自动找空位，找不到空位就叠放上去</strong>。
    /// <para>
    /// 第 42 棒翻掉的老规矩是「纸面没有空位就返回 null（不给加）」，它在 AI 行式模板上必然触发：
    /// <see cref="RowLayoutSpec.Build"/> 出的每一行文字都是<strong>全宽行带</strong>（X=留白、宽=可用宽、高=整条行带），
    /// 四行铺满 140×100 之后行与行之间只剩 2mm 缝，而新建文本默认 6mm 高、判重叠还要外加
    /// <see cref="MinGapMm"/> 呼吸间距 —— 于是「加个文字/加张图」永远失败，用户看到的却是一大片空白。
    /// 界面还把这个失败报成「一张标签最多 80 个元素」，而那份模板只有 4~6 个元素：
    /// 两种失败（真撞上限 / 找不到空位）被混进同一句话，数字纯属误导。
    /// </para>
    /// <para>
    /// 现在的口径与 CorelDRAW / PPT 一致：<strong>加东西这个动作只有「真到 80 个上限」一种失败</strong>，
    /// 其余一律加上去，叠在现有内容上也加，由 <see cref="AddElementOutcome.Overlapped"/> 告知界面提醒用户拖开。
    /// 叠放位置仍夹紧到纸内 —— 越界会让 <see cref="TemplateValidator"/> 报 Error 而存不了盘，那不是提醒是死路。
    /// </para>
    /// </summary>
    /// <returns>落位结果；<c>null</c> 只表示元素数量已达 <see cref="TemplateValidator.MaxElements"/>。</returns>
    /// <param name="exactlyWhereAsked">true = <strong>用户点哪就落哪</strong>：不找空位、不夹进纸内。
    /// <para>画布上拖出来的形状与曲线走这条（第 81 棒④，用户：「在画时被固定边框为起点，不是实际鼠标点击起始位置」）：
    /// 那一点是他下的手，搬走它就是"起点不跟手"。而且 AI 行式模板每一行都是通栏行带，满纸都"重叠"，
    /// <see cref="FindFreeSpot"/> 必然把他按下的那一点搬到 2mm 网格上的别处。</para>
    /// <para>工具栏那颗「添加文本/图片」仍走 false：那里的位置是<strong>软件猜的</strong>，猜的位置该躲开别人。</para></param>
    public static AddElementOutcome? AddElement(LabelTemplate template, TemplateElement element,
        double xPreferred, double yPreferred, bool exactlyWhereAsked = false)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (template.Elements.Count >= TemplateValidator.MaxElements) return null;

        var (oldX, oldY) = (element.X, element.Y);
        if (exactlyWhereAsked)
        {
            element.X = xPreferred;
            element.Y = yPreferred;
        }
        else
        {
            var spot = FindFreeSpot(template, element, xPreferred, yPreferred)
                       ?? ClampedSpot(template, element, xPreferred, yPreferred);
            element.X = Math.Max(0, Math.Min(spot.X, Math.Max(0, template.WidthMm - EditGeometry.BoxOf(element).Width)));
            element.Y = Math.Max(0, Math.Min(spot.Y, Math.Max(0, template.HeightMm - EditGeometry.BoxOf(element).Height)));
        }
        if (element.Kind == ElementKind.Line)
        {
            // 按真实位移挪另一端。从前这里写的是「X2 = 新X + (X2 - 新X)」= 原地不动，
            // 结果落点一夹、线被拉长或压短（旧位置已经被覆盖掉了，长度量不出来）。
            var dx = element.X - oldX;
            var dy = element.Y - oldY;
            element.X2 += dx;
            element.Y2 += dy;
            CurveGeometry.Translate(element, dx, dy);      // 曲线：中间节点跟着走，不然弧身留在原地
        }

        // 必须在 Add 之前判：加进去之后再判会拿自己跟自己比，永远"重叠"。
        var overlapped = OverlapsAny(template, element, element.X, element.Y);

        template.Elements.Add(element);
        return new AddElementOutcome(template.Elements.Count - 1, overlapped);
    }

    /// <summary>加一个绑定字段的文本元素（编辑器「插入字段」按钮走这里）。</summary>
    public static AddElementOutcome? AddFieldText(LabelTemplate template, string token, double widthMm = 40, double heightMm = 6, double fontPt = 8)
    {
        var field = $"{{{{{token}}}}}";
        return AddElement(template, NewText(field, template.PaddingMm, template.PaddingMm, widthMm, heightMm, fontPt),
            template.PaddingMm, template.PaddingMm);
    }

    /// <summary>
    /// 拖动两点 → 矩形（毫米）：以按下的那一角为锚，拖到哪算哪。
    /// <para>往左上、左下拖都成立（负向要翻回左上角并取绝对值），边长最低 <see cref="MinSideMm"/>
    /// ——零尺寸的框既画不出来也存不住，校验器会直接报错。</para>
    /// </summary>
    public static (double X, double Y, double Width, double Height) RectFromCorners(
        double anchorX, double anchorY, double xMm, double yMm)
        => BoxFromCorners(anchorX, anchorY, xMm, yMm);

    /// <summary>
    /// 两点算盒的<strong>唯一出处</strong>（矩形/椭圆/多边形共用，第 51 棒把矩形那条泛化过来）。
    /// <para>CorelDRAW 的两个修饰键照它自带文案实现：<strong>Ctrl＝限制为圆/正方</strong>
    /// （「按住 Ctrl 键拖动可限制为圆形」——两轴取更大的那个拖幅，方向照拖的那边走）；
    /// <strong>Shift＝从中心绘制</strong>（「按住 Shift 键并拖动可从中心绘制」——按下的点是中心，四向对称张开）。</para>
    /// </summary>
    public static (double X, double Y, double Width, double Height) BoxFromCorners(
        double anchorX, double anchorY, double xMm, double yMm, bool fromCenter = false, bool square = false)
    {
        double dx = xMm - anchorX, dy = yMm - anchorY;
        if (square)
        {
            // 两轴同幅：取拖得更远的那一轴为准（另一轴跟上），符号保留——往哪拖还往哪长。
            var side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = Math.Sign(dx == 0 ? 1 : dx) * side;
            dy = Math.Sign(dy == 0 ? 1 : dy) * side;
        }
        if (fromCenter)
            return (anchorX - Math.Abs(dx), anchorY - Math.Abs(dy),
                Math.Max(EditGeometry.MinSideMm, Math.Abs(dx) * 2), Math.Max(EditGeometry.MinSideMm, Math.Abs(dy) * 2));
        return (Math.Min(anchorX, anchorX + dx), Math.Min(anchorY, anchorY + dy),
            Math.Max(EditGeometry.MinSideMm, Math.Abs(dx)), Math.Max(EditGeometry.MinSideMm, Math.Abs(dy)));
    }

    /// <summary>新形状元素（第 51 棒）：矩形/椭圆/多边形都住外接盒，外观字段（填充/描边/线宽）三者共用。</summary>
    public static TemplateElement NewShape(ElementKind kind, double x, double y, double width, double height,
        double thicknessMm = 0.35)
        => new()
        {
            Kind = kind,
            X = x,
            Y = y,
            Width = Math.Max(EditGeometry.MinSideMm, width),
            Height = Math.Max(EditGeometry.MinSideMm, height),
            ThicknessMm = thicknessMm,
        };

    /// <summary>找不到空位时的兜底落点：把偏好位置夹进纸内，<strong>不拒绝、不丢弃</strong>。
    /// <para>元素本身比标签还大时夹到 (0,0) 并交给校验器报越界 —— 那种情况看得见、说得清，
    /// 比"点了没反应"强（§五-80：判据写好了没人读等于没有判据）。</para>
    /// </summary>
    private static (double X, double Y) ClampedSpot(LabelTemplate template, TemplateElement element, double preferredX, double preferredY)
    {
        var box = EditGeometry.BoxOf(element);
        var maxX = Math.Max(0, template.WidthMm - box.Width);
        var maxY = Math.Max(0, template.HeightMm - box.Height);
        return (Math.Clamp(preferredX, 0, maxX), Math.Clamp(preferredY, 0, maxY));
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
            CurveGeometry.Translate(copy, offsetMm, offsetMm);
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

/// <summary>
/// 一次「加元素」的落位结果：<strong>加成功了一定有下标</strong>，另带一句"是不是叠在别人身上了"。
/// <para>第 42 棒：原来是 <c>int?</c>，null 同时表示"到 80 个上限"与"纸面没空位"两种完全不同的事，
/// 界面分不清就一律报「最多 80 个元素」，用户对着只有 4 个元素的模板看到 80，只会以为软件坏了。</para>
/// </summary>
/// <param name="Index">新元素在 <c>Elements</c> 里的下标。</param>
/// <param name="Overlapped">true = 落点与已有可见元素重叠（叠放上去了，提示用户拖开）。</param>
public sealed record AddElementOutcome(int Index, bool Overlapped);
