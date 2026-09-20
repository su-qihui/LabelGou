using LabelGou.Core.Interop.Cdr;

namespace LabelGou.Core.Templates;

/// <summary>
/// cdrx 逐对象 → 模板元素（第 88 棒）。
/// <para>用户口径：「<strong>导入 CDR 文件就按照 CDR 文件原模原样复刻，不受软件约束</strong>」。
/// 所以这一层<strong>不搬位、不夹尺寸、不改字号、不重排换行、不为凑上限删对象</strong>；
/// 表达不了的形态一律降级成<strong>看得见的占位</strong>并把原因写进
/// <see cref="TemplateElement.SourceNotes"/>，绝不静默少一个（"读不出来"≠"它不存在"）。</para>
/// </summary>
public static class CdrxElementBuilder
{
    /// <param name="doc">读出来的 cdrx。</param>
    /// <param name="issues">整份层面的降级与告警往这里追加（界面与导出摘要都读它）。</param>
    /// <param name="saveImage">
    /// 把 cdrx 目录里的位图文件搬进模板 assets：入参是 <c>*.cdrx.json</c> 旁边的相对路径，
    /// 返回模板用的相对路径；<strong>返回 null 表示这张图没拿到</strong>——那时元素降级成占位而不是消失。
    /// </param>
    /// <param name="resolveFont">
    /// 字体替代：入参是 Corel 里记的字体名，返回 null 表示本机装了照原样用，返回一个名字表示本机没装、
    /// 用它替（App 层拿系统字体表来答，Core 不碰 WPF）。<strong>替了谁必须写进 SourceNotes</strong>。
    /// </param>
    public static CdrxBuildResult Build(CdrxDoc doc, ICollection<TemplateIssue> issues, Func<string, string?>? saveImage = null,
        Func<string, string?>? resolveFont = null)
    {
        var elements = new List<TemplateElement>();
        var texts = new List<TemplateElement>();
        foreach (var (o, trail) in InPaintOrder(doc.Objects, ""))
        {
            var e = Of(o, trail, issues, saveImage, resolveFont);
            if (e is null) continue;
            elements.Add(e);
            if (e.Kind == ElementKind.Text) texts.Add(e);
        }

        foreach (var line in doc.Source.Degraded)
            issues.Add(new TemplateIssue(IssueLevel.Info, $"来源降级（{doc.Source.Kind}）：{line}"));

        if (elements.Count == 0 && doc.Objects.Count > 0)
            issues.Add(new TemplateIssue(IssueLevel.Warning,
                $"这份 cdrx 报了 {doc.Objects.Count} 个顶层对象，但一个都没落成元素——去看 source.degraded 与各对象的 notes，别当它是空图纸。"));
        return new CdrxBuildResult(elements, texts);
    }

    /// <summary>
    /// 按 Corel 的 z 序摆绘制顺序（小的先画＝在下面），群组就地展平。
    /// <para>展平是第 88 棒定的口径：模板模型是扁平列表＋落位下标，没有组概念。
    /// 代价是"整组一起挪"的手感没了，所以<see cref="TemplateElement.SourceNotes"/> 里留下它原本在哪个组。</para>
    /// </summary>
    static IEnumerable<(CdrxObject Object, string Trail)> InPaintOrder(IEnumerable<CdrxObject> objs, string trail)
    {
        foreach (var o in objs.OrderBy(x => x.Z ?? 0))
        {
            if (o.Kind == CdrxKinds.Group && o.Children is { Count: > 0 })
            {
                var inner = trail.Length == 0 ? $"群组#{o.Z?.ToString() ?? "?"}" : $"{trail}›群组#{o.Z?.ToString() ?? "?"}";
                foreach (var child in InPaintOrder(o.Children, inner)) yield return child;
                continue;
            }
            yield return (o, trail);
        }
    }

    static TemplateElement? Of(CdrxObject o, string trail, ICollection<TemplateIssue> issues, Func<string, string?>? saveImage,
        Func<string, string?>? resolveFont)
    {
        var notes = new List<string>();
        if (trail.Length > 0) notes.Add($"展平自 {trail}（模板里没有组，整组一起挪的手感暂时丢了）");
        if (o.Notes is { Count: > 0 }) notes.AddRange(o.Notes);
        if (o.Layer is { Length: > 0 } layer && layer != "图层 1") notes.Add($"原图层「{layer}」");

        if (o.Box is not { } box)
            return Undrawable(o, notes.Append("这个对象连盒都没报——位置无从谈起，先占个位").ToList());

        // 旋转：只有拿到未旋转框才许带角度。否则 box 已经是转完的视觉盒，再转一遍就是把旋转算两遍
        // （CDR 分区 §九-P14/D2 同一件事，这边从消费侧再钉一次）。
        var place = o.UnrotatedBox ?? box;
        var rotation = o.UnrotatedBox is null ? 0 : o.Rot ?? 0;
        if (o.UnrotatedBox is null && Math.Abs(o.Rot ?? 0) > 1e-6)
            notes.Add($"Corel 报它转了 {o.Rot:0.##}°，但只给了转完的视觉盒：这里按盒直接摆、不再套旋转");

        TemplateElement? el = o.Kind switch
        {
            CdrxKinds.Text when o.Text is { } t => Text(o, t, place, notes, resolveFont),
            CdrxKinds.Rect => Shape(o, ElementKind.Rect, box, place, rotation, notes),
            CdrxKinds.Ellipse => Shape(o, ElementKind.Ellipse, box, place, rotation, notes),
            CdrxKinds.Polygon => Polygon(o, box, place, rotation, notes),
            CdrxKinds.Line => Line(o, box, notes),
            CdrxKinds.Curve => Curve(o, box, notes),
            CdrxKinds.Image => Image(o, box, place, rotation, notes, saveImage),
            _ => Undrawable(o, notes.Append($"Corel 这一类（kind={o.Kind}）本软件还没有画法，先占位").ToList()),
        };
        if (el is null) return null;

        el.X = place.X;
        el.Y = place.Y;
        el.Width = place.W;
        el.Height = place.H;
        el.RotationDeg = rotation;
        el.Imported = true;
        el.Visible = o.IsVisible;
        if (!o.IsVisible) notes.Add("在 CorelDRAW 里就是隐形对象（无填充无描边）：留着占位与越界判断，画面上不该出现");
        // Corel 的横向/纵向拉伸：文字这一族模板模型有对应自由度（TextScaleX/Y），照搬过去。
        // 群组成员除外——它的 trfd 是局部量还是全页量没定案（OLU 组内那只矩形报的是 2.55×1.35，
        // 那是群组自己的缩放；盒已经是转完的范围，再乘一次就是把同一个缩放算两遍），所以只登记不采用。
        var rawSx = o.ScaleX ?? 1;
        var rawSy = o.ScaleY ?? 1;
        var stretched = Math.Abs(rawSx - 1) > 1e-6 || Math.Abs(rawSy - 1) > 1e-6;
        if (stretched && trail.Length > 0)
            notes.Add($"它在群组里，Corel 对这一层报的拉伸是 横 {rawSx:0.####} / 纵 {rawSy:0.####} —— 群组成员的矩阵" +
                      "是局部量还是全页量还没定案，而它的盒已经是转完的范围：这里按盒画，不重复乘");
        else if (stretched && el.Kind == ElementKind.Text)
        {
            el.TextScaleX = rawSx;
            el.TextScaleY = rawSy;
            notes.Add($"Corel 把它拉伸到 横 {rawSx * 100:0.##}% / 纵 {rawSy * 100:0.##}%，已按原样带上");
        }
        else if (stretched && el.Kind == ElementKind.Polygon)
            notes.Add($"Corel 把它拉伸到 横 {rawSx * 100:0.##}% / 纵 {rawSy * 100:0.##}%，" +
                      "而本软件的多边形只有「边数 + 盒」，非等比拉完它就不再是正多边形：这一条按盒画内接的那个");
        // Rect / Ellipse / Image 不点名：它们的几何就是那个盒，盒已经带着比例，什么都没丢
        if (notes.Count > 0) el.SourceNotes = notes;
        return el;
    }

    static TemplateElement Text(CdrxObject o, CdrxText t, CdrxBox place, List<string> notes, Func<string, string?>? resolveFont)
    {
        var font = t.Font;
        var wanted = font?.Name;
        var substitute = string.IsNullOrWhiteSpace(wanted) ? null : resolveFont?.Invoke(wanted!);
        if (string.IsNullOrWhiteSpace(wanted))
            notes.Add("Corel 报回空字体名：落到默认字体" + TemplateElement.DefaultFont +
                      "，字身形状与原来对不上（装了原字库再重导一次才有准数）");
        else if (substitute is not null)
            notes.Add($"Corel 用的字体「{wanted}」这台机器没装：已换成「{substitute}」显示与出片——" +
                      "字身与每行宽度都会和 Corel 里有出入（要一模一样得装原字库）");
        var lines = t.Lines.Length;
        var knownSize = font?.SizePt is double pt && pt > 0;
        // 字号已知是常态了：B 通道第 88 棒把 stlt 样式链走通（txsm 段落 style_id → records → fonts → 字号 raw），
        // 六份真样件与 A(COM) 逐只对过，Δ≤0.0005 pt。剩下的 <b>未知</b> 只来自更窄的来源（旧 cdrx 文件、
        // 或 A 侧 Corel 没报字号的对象）——那种只能由盒高反推：盒高含行距，直接拿行高当字身会系统性偏大
        // （金沐实测 obj0 偏大 12.7%、obj1 偏小 21%，两个方向都错），所以除以常用行距 1.2 并放开"按盒宽缩字"。
        var guessedPt = Math.Max(1, place.H / Math.Max(1, lines) / 1.2 / 0.3528);
        var el = new TemplateElement
        {
            Kind = ElementKind.Text,
            Text = t.Contents,
            FontFamily = string.IsNullOrWhiteSpace(wanted) ? TemplateElement.DefaultFont : (substitute ?? wanted!),
            FontSizePt = knownSize ? font!.SizePt!.Value : guessedPt,
            Bold = font?.Bold ?? false,
            Align = HorizontalAlign.Left,
            // 「原模原样」：不许重排断行。字号已知时连缩字都不开。
            AllowWrap = false,
            ShrinkToFit = knownSize,
            WrapWidthMm = knownSize ? 0 : Math.Max(0, place.W),
            MaxLines = lines,
            InkColor = ColorOf(o.Ink) ?? ColorOf(o.Stroke?.Color),
        };
        if (!knownSize)
            notes.Add("这份来源没报出字号（不是「它没有字号」）：按盒高含行距反推，并放开缩字让它塞回原来那一格——" +
                      "字身大小与 Corel 里会有出入，位置与断行是准的");
        // 对齐读不出来这件事必须点名：艺术字的外框就是最长那行的墨迹宽，
        // 短行在 Corel 里按各自的对齐方式落，一律画成左对齐时长行看着对、短行整体偏左。
        notes.Add("Corel 的段落对齐在 txtj 块里，本版没定出语义：一律按左对齐落位。" +
                  "若原稿是居中/右对齐，短的那几行会偏——选中这一条改右侧「对齐」就行，位置与字号不受影响");
        return el;
    }

    static TemplateElement Shape(CdrxObject o, ElementKind kind, CdrxBox box, CdrxBox place, double rotation, List<string> notes)
    {
        var el = new TemplateElement
        {
            Kind = kind,
            FillColor = ColorOf(o.Ink),
        };
        ApplyStroke(o, el, notes);
        if (kind == ElementKind.Rect)
        {
            var (cw, ch) = (o.Geom?.CornerWMm, o.Geom?.CornerHMm);
            if (cw is > 0 || ch is > 0)
            {
                el.CornerRadiusMm = cw ?? ch ?? 0;
                if (cw is not null && ch is not null && Math.Abs(cw.Value - ch.Value) > 0.01)
                    notes.Add($"Corel 的圆角是椭圆角（宽 {cw:0.##}×高 {ch:0.##} mm），本软件只有单一半径：按宽那个画");
            }
        }
        return el;
    }

    static TemplateElement? Polygon(CdrxObject o, CdrxBox box, CdrxBox place, double rotation, List<string> notes)
    {
        var sides = o.Geom?.Sides;
        if (sides is null || sides < ShapeGeometry.MinSides || sides > ShapeGeometry.MaxSides)
        {
            notes.Add($"多边形边数没读到（拿到的是 {sides?.ToString() ?? "无"}）：画不出它的轮廓，先占位");
            return Undrawable(o, notes, box, place, rotation);
        }
        var el = Shape(o, ElementKind.Polygon, box, place, rotation, notes);
        el.PolygonSides = sides;
        return el;
    }

    static TemplateElement? Line(CdrxObject o, CdrxBox box, List<string> notes)
    {
        // cdrx 的 line 只有盒；一条线要两个端点。把盒当成水平线画会凭空造出几何来，所以占位。
        notes.Add("CDR 报的是一条线段，但这份中间格式只给了盒没给两个端点：不猜它的走向，先占位");
        return Undrawable(o, notes, box, box, 0);
    }

    static TemplateElement? Curve(CdrxObject o, CdrxBox box, List<string> notes)
    {
        var subs = o.Curve?.Subpaths;
        var nodes = subs?.Sum(s => s.Nodes.Count) ?? 0;
        notes.Add($"曲线（{(subs?.Count ?? 0)} 条子路径、{nodes} 个节点）这一版还没接进模板模型——" +
                  "它只支持单条开放/闭合曲线，多子路径要先拆成几条才算得清；先占位，不当它不存在");
        return Undrawable(o, notes, box, box, 0);
    }

    static TemplateElement? Image(CdrxObject o, CdrxBox box, CdrxBox place, double rotation, List<string> notes, Func<string, string?>? saveImage)
    {
        var file = o.Image?.File;
        var relative = string.IsNullOrWhiteSpace(file) ? null : saveImage?.Invoke(file!);
        if (relative is null)
        {
            notes.Add($"位图对象拿不到文件（cdrx 记的是「{file ?? "没记文件名"}」）：位置尺寸照原样占位，图还没进来");
            return Undrawable(o, notes, box, place, rotation);
        }
        return new TemplateElement { Kind = ElementKind.Image, ImagePath = relative };
    }

    static TemplateElement? Undrawable(CdrxObject o, List<string> notes, CdrxBox? box = null, CdrxBox? place = null, double rotation = 0)
    {
        var b = place ?? box ?? o.Box;
        if (b is null) return null;      // 连盒都没有：这一条确实摆不下，但调用方已按"一个都没落成"报了警
        return new TemplateElement
        {
            Kind = ElementKind.Placeholder,
            X = b.X, Y = b.Y, Width = b.W, Height = b.H,
            RotationDeg = rotation,
            Visible = o.IsVisible,
            Imported = true,
            SourceNotes = notes,
        };
    }

    static void ApplyStroke(CdrxObject o, TemplateElement el, List<string> notes)
    {
        var stroke = o.Stroke;
        if (stroke is null) return;
        var color = ColorOf(stroke.Color);
        var on = stroke.On ?? (stroke.WidthMm is > 0 || color is not null);
        if (!on)
        {
            el.Stroked = false;          // 关描边才上盘（与 Stroked 既有写法一致）
            return;
        }
        if (color is not null) el.InkColor = color;
        else notes.Add("有描边但描边色没读到：按黑画（Corel 报的是 unnamed color，那不是颜色）");
        el.ThicknessMm = stroke.WidthMm is double w && w > 0 ? w : 0.35;
    }

    /// <summary>
    /// cdrx 的色 → 主软件的 <see cref="Colors.LabelColor"/>。
    /// <para>CMYK 优先（印刷口径的原值）；只有屏幕色且<strong>不是派生的</strong>才按 RGB 落——
    /// 那条 <c>rgbDerived</c> 标记就是为了这里不拿近似值当真值。</para>
    /// </summary>
    static Colors.LabelColor? ColorOf(CdrxColor? c)
    {
        if (c is null) return null;
        if (c.Cmyk is { Length: 4 } k)
            return Colors.LabelColor.FromCmyk(Round(k[0]), Round(k[1]), Round(k[2]), Round(k[3]));
        if (c.Rgb is { Length: 7 } hex && !c.RgbDerived && Colors.LabelColor.TryParse(hex, out var parsed))
            return parsed;
        return null;
    }

    static int Round(double percent) => Math.Clamp((int)Math.Round(percent), 0, 100);
}

/// <summary>
/// 一次 cdrx 逐对象落地的结果。文字那几条单独再列一遍：导入窗口靠同一批引用做「勾选提升 / 改绑字段」，
/// 靠引用而不是下标，免得中间有人被丢掉时两边错位。
/// </summary>
public sealed record CdrxBuildResult(IReadOnlyList<TemplateElement> Elements, IReadOnlyList<TemplateElement> TextElements);
