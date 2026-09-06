using LabelGou.Core.Editing;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// B 类模板编辑器的几何与历史。
/// <para>
/// 这些规则决定"拖一下之后还印不印得出来"，全在 Core 里就是为了能自动化验证：
/// 越界必须贴边而不是消失、撤销必须真的回到旧值（快照要和在编对象断开）、
/// 新建元素不能压在别人身上。这些靠手点 GUI 是发现不全的。
/// </para>
/// </summary>
public class EditingTests
{
    private static LabelTemplate Page(double width = 100, double height = 80, double padding = 4) => new()
    {
        Id = "user.test",
        Name = "测试模板",
        WidthMm = width,
        HeightMm = height,
        PaddingMm = padding,
        BuiltIn = false,
    };

    private static TemplateElement Box(double x, double y, double w, double h, ElementKind kind = ElementKind.Rect)
        => new() { Kind = kind, X = x, Y = y, Width = w, Height = h };

    private static TemplateElement Line(double x1, double y1, double x2, double y2)
        => new() { Kind = ElementKind.Line, X = x1, Y = y1, X2 = x2, Y2 = y2, Width = Math.Abs(x2 - x1), Height = Math.Abs(y2 - y1) };

    // ---------- 包围盒与命中 ----------

    [Fact]
    public void LineBoundingBoxComesFromEndpoints()
    {
        var box = EditGeometry.BoxOf(Line(30, 50, 10, 20));
        Assert.Equal(10, box.X);
        Assert.Equal(20, box.Y);
        Assert.Equal(20, box.Width);
        Assert.Equal(30, box.Height);
    }

    [Fact]
    public void HitTestRespectsToleranceOutsideTheBox()
    {
        var element = Box(10, 10, 20, 8);
        Assert.True(EditGeometry.HitTest(element, 25, 14));
        Assert.True(EditGeometry.HitTest(element, 9.6, 14), "差 0.4mm 应当算点中，手指没那么准");
        Assert.False(EditGeometry.HitTest(element, 9.0, 14));
    }

    [Fact]
    public void ThinLineIsStillClickable()
    {
        var line = Line(0, 20, 100, 20);
        line.ThicknessMm = 0.3;
        Assert.True(EditGeometry.HitTest(line, 50, 20.4), "0.3mm 的线按线宽放宽容差");
        Assert.False(EditGeometry.HitTest(line, 50, 25));
    }

    [Fact]
    public void TopmostElementWinsTheClick()
    {
        var template = Page();
        var bottom = Box(5, 5, 40, 40);
        var top = Box(20, 20, 10, 10);
        template.Elements.Add(bottom);
        template.Elements.Add(top);

        Assert.Equal(1, EditGeometry.TopmostAt(template, 22, 22));
        Assert.Equal(0, EditGeometry.TopmostAt(template, 8, 8));
        Assert.Equal(-1, EditGeometry.TopmostAt(template, 80, 70));
    }

    [Fact]
    public void InvisibleElementsDoNotCatchClicks()
    {
        var template = Page();
        template.Elements.Add(Box(5, 5, 40, 40));
        var hidden = Box(20, 20, 10, 10);
        hidden.Visible = false;
        template.Elements.Add(hidden);

        Assert.Equal(0, EditGeometry.TopmostAt(template, 22, 22));
    }

    [Fact]
    public void HandlesDistinguishCornerFromEdge()
    {
        var element = Box(10, 10, 20, 8);
        Assert.Equal(ResizeHandle.TopLeft, EditGeometry.HandleAt(element, 10, 10, 1));
        Assert.Equal(ResizeHandle.BottomRight, EditGeometry.HandleAt(element, 30, 18, 1));
        Assert.Equal(ResizeHandle.Left, EditGeometry.HandleAt(element, 10, 14, 1));
        Assert.Equal(ResizeHandle.Top, EditGeometry.HandleAt(element, 20, 10, 1));
        Assert.Equal(ResizeHandle.None, EditGeometry.HandleAt(element, 20, 14, 1));
    }

    [Fact]
    public void LineEndpointsAreTheHandles()
    {
        var line = Line(10, 10, 60, 30);
        Assert.Equal(ResizeHandle.LineStart, EditGeometry.HandleAt(line, 10, 10, 1));
        Assert.Equal(ResizeHandle.LineEnd, EditGeometry.HandleAt(line, 60, 30, 1));
        Assert.Equal(ResizeHandle.None, EditGeometry.HandleAt(line, 35, 20, 1));
    }

    // ---------- 移动 ----------

    [Fact]
    public void DraggingPastTheEdgeStopsAtTheBorder()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));

        var shift = EditGeometry.MoveBy(template, 0, 200, 200);

        Assert.Equal(80, template.Elements[0].X, 6);
        Assert.Equal(72, template.Elements[0].Y, 6);
        Assert.Equal(70, shift.Dx, 6);
        Assert.True(shift.Dx < 200, "报回来的位移要小于请求量，UI 才知道被挡住了");
    }

    [Fact]
    public void MovingLineShiftsBothEndpoints()
    {
        var template = Page();
        template.Elements.Add(Line(10, 10, 60, 30));

        EditGeometry.MoveBy(template, 0, 5, -4);

        Assert.Equal(15, template.Elements[0].X, 6);
        Assert.Equal(6, template.Elements[0].Y, 6);
        Assert.Equal(65, template.Elements[0].X2, 6);
        Assert.Equal(26, template.Elements[0].Y2, 6);
    }

    [Fact]
    public void OversizedElementDoesNotBreakClamping()
    {
        var template = Page(width: 30, height: 30);
        template.Elements.Add(Box(0, 0, 60, 60));

        var moved = EditGeometry.MoveTo(template, 0, 500, 500);

        Assert.Equal(0, moved.Item1, 6);
        Assert.Equal(0, moved.Item2, 6);
    }

    // ---------- 缩放 ----------

    [Fact]
    public void ResizeKeepsMinimumSide()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));

        EditGeometry.ResizeBy(template, 0, ResizeHandle.Left, 50, 0);

        Assert.Equal(29.2, template.Elements[0].X, 6);
        Assert.Equal(EditGeometry.MinSideMm, template.Elements[0].Width, 6);
    }

    [Fact]
    public void ResizeCannotDragOffThePaper()
    {
        var template = Page();
        template.Elements.Add(Box(60, 10, 20, 8));

        EditGeometry.ResizeBy(template, 0, ResizeHandle.Right, 50, 0);

        Assert.Equal(100, template.Elements[0].X + template.Elements[0].Width, 6);
    }

    [Fact]
    public void LineResizeOnlyMovesTheGrabbedEnd()
    {
        var template = Page();
        template.Elements.Add(Line(10, 10, 60, 30));

        EditGeometry.ResizeBy(template, 0, ResizeHandle.LineEnd, 5, 5);

        Assert.Equal(10, template.Elements[0].X, 6);
        Assert.Equal(65, template.Elements[0].X2, 6);
        Assert.Equal(35, template.Elements[0].Y2, 6);
    }

    // ---------- 吸附 ----------

    [Fact]
    public void SnapToLabelCenterLine()
    {
        var template = Page();
        template.Elements.Add(Box(39.5, 10, 20, 6));

        var result = EditGeometry.Snap(template, 0, 39.5, 10, new SnapOptions { SnapToGrid = false });

        Assert.Equal(40, result.X, 6);
        var guide = Assert.Single(result.Guides);
        Assert.True(guide.Vertical);
        Assert.Equal(GuideSource.LabelCenter, guide.Source);
    }

    [Fact]
    public void SnapToPaddingLine()
    {
        var template = Page();
        template.Elements.Add(Box(4.4, 30, 20, 6));

        var result = EditGeometry.Snap(template, 0, 4.4, 30, new SnapOptions { SnapToGrid = false });

        Assert.Equal(4, result.X, 6);
        Assert.Equal(GuideSource.Padding, Assert.Single(result.Guides).Source);
    }

    [Fact]
    public void SnapToNeighbourEdge()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        template.Elements.Add(Box(30.2, 55, 20, 8));

        var result = EditGeometry.Snap(template, 1, 30.2, 55, new SnapOptions { SnapToGrid = false });

        Assert.Equal(30, result.X, 6);
        Assert.Equal(55, result.Y, 6);
        Assert.Equal(GuideSource.Neighbor, Assert.Single(result.Guides).Source);
    }

    [Fact]
    public void GridRoundingWhenNothingIsNear()
    {
        var template = Page();
        template.Elements.Add(Box(10.2, 60.3, 20, 6));

        var result = EditGeometry.Snap(template, 0, 10.2, 60.3, new SnapOptions { GridStepMm = 1 });

        Assert.Equal(10, result.X, 6);
        Assert.Equal(60, result.Y, 6);
        Assert.Empty(result.Guides);
    }

    [Fact]
    public void SnappedPositionIsStillClampedInsideThePaper()
    {
        var template = Page();
        template.Elements.Add(Box(90, 10, 10, 10));
        template.Elements.Add(Box(89.6, 40, 15, 6));

        var result = EditGeometry.Snap(template, 1, 89.6, 40, new SnapOptions { SnapToGrid = false });

        Assert.Equal(85, result.X, 6);
        Assert.True(result.X + 15 <= template.WidthMm + EditGeometry.HitToleranceMm);
    }

    [Fact]
    public void SnapCanBeTurnedOff()
    {
        var template = Page();
        template.Elements.Add(Box(39.5, 10, 20, 6));

        var result = EditGeometry.Snap(template, 0, 39.5, 10, SnapOptions.None);

        Assert.Equal(39.5, result.X, 6);
        Assert.Empty(result.Guides);
    }

    // ---------- 对齐与层级 ----------

    [Theory]
    [InlineData(AlignHorizontal.Left, 0)]
    [InlineData(AlignHorizontal.Center, 40)]
    [InlineData(AlignHorizontal.Right, 80)]
    public void AlignHorizontallyAgainstTheLabel(AlignHorizontal horizontal, double expectedX)
    {
        var template = Page();
        template.Elements.Add(Box(15, 10, 20, 8));

        EditGeometry.AlignToLabel(template, 0, horizontal, AlignVertical.Top);

        Assert.Equal(expectedX, template.Elements[0].X, 6);
        Assert.Equal(0, template.Elements[0].Y, 6);
    }

    [Fact]
    public void AlignUsesBoundingBoxForLines()
    {
        var template = Page();
        template.Elements.Add(Line(10, 70, 50, 74));

        EditGeometry.AlignToLabel(template, 0, AlignHorizontal.Right, AlignVertical.Top);

        Assert.Equal(60, template.Elements[0].X, 6);
        Assert.Equal(100, template.Elements[0].X2, 6);
        Assert.Equal(0, template.Elements[0].Y, 6);
        Assert.Equal(4, template.Elements[0].Y2, 6);
    }

    [Fact]
    public void SnapToPaddingMovesIntoTheContentArea()
    {
        var template = Page(padding: 6);
        template.Elements.Add(Box(0, 0, 20, 8));

        EditGeometry.SnapToPadding(template, 0, AlignHorizontal.Right, AlignVertical.Bottom);

        Assert.Equal(74, template.Elements[0].X, 6);
        Assert.Equal(66, template.Elements[0].Y, 6);
    }

    [Fact]
    public void LayerOrderMovesTheElement()
    {
        var template = Page();
        var first = Box(5, 5, 10, 10);
        var second = Box(20, 20, 10, 10);
        var third = Box(35, 35, 10, 10);
        template.Elements.AddRange(new[] { first, second, third });

        Assert.Equal(1, EditGeometry.MoveLayer(template, 0, 1));
        Assert.Same(second, template.Elements[0]);
        Assert.Same(first, template.Elements[1]);

        // 现在列表是 [second, first, third]：把最上面的 second 再置底
        Assert.Equal(2, EditGeometry.BringToFront(template, 0));
        Assert.Same(second, template.Elements[^1]);

        Assert.Equal(0, EditGeometry.SendToBack(template, 2));
        Assert.Same(second, template.Elements[0]);
    }

    [Fact]
    public void BadIndexReportsReadableError()
    {
        var template = Page();
        template.Elements.Add(Box(5, 5, 10, 10));

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => EditGeometry.MoveBy(template, 7, 1, 1));
        Assert.Contains("超出范围", error.Message);
    }

    // ---------- 撤销 / 重做 ----------

    [Fact]
    public void UndoRestoresTheValueBeforeTheChange()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory();

        history.Capture(template);
        template.Elements[0].X = 55;
        var undone = history.Undo(template);

        Assert.True(undone);
        Assert.Equal(10, template.Elements[0].X, 6);
    }

    [Fact]
    public void SnapshotsAreDetachedFromTheWorkingTemplate()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory();

        history.Capture(template);
        template.Elements[0].X = 55;
        template.Elements[0].Text = "改了也不该影响快照";
        history.Undo(template);

        Assert.Equal(10, template.Elements[0].X, 6);
        Assert.Null(template.Elements[0].Text);
    }

    [Fact]
    public void RedoBringsTheChangeBack()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory();

        history.Capture(template);
        template.Elements[0].X = 55;
        history.Undo(template);

        Assert.True(history.Redo(template));
        Assert.Equal(55, template.Elements[0].X, 6);
    }

    [Fact]
    public void NewChangeClearsTheRedoStack()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory();

        history.Capture(template);
        template.Elements[0].X = 20;
        history.Undo(template);
        history.Capture(template);

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void HistoryStopsAtTheLimitAndDropsTheOldest()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory(limit: 3);

        for (var i = 1; i <= 5; i++)
        {
            history.Capture(template);
            template.Elements[0].X = i * 5;
        }

        Assert.Equal(3, history.UndoCount);
        history.Undo(template);
        Assert.Equal(20, template.Elements[0].X, 6);
    }

    [Fact]
    public void UndoOnEmptyStackChangesNothing()
    {
        var template = Page();
        template.Elements.Add(Box(10, 10, 20, 8));
        var history = new TemplateHistory();

        Assert.False(history.Undo(template));
        Assert.False(history.Redo(template));
        Assert.Equal(10, template.Elements[0].X, 6);
    }

    [Fact]
    public void CopyIntoKeepsTheSameInstanceForTheUi()
    {
        var source = Page();
        source.Elements.Add(Box(12, 13, 20, 8));
        var target = Page();
        target.Elements.Add(Box(99, 99, 5, 5));

        source.CopyInto(target);

        Assert.Equal("user.test", target.Id);
        Assert.Single(target.Elements);
        Assert.Equal(12, target.Elements[0].X, 6);
        Assert.NotSame(source.Elements[0], target.Elements[0]);
    }

    // ---------- 工厂 ----------

    [Fact]
    public void BlankTemplatePassesValidation()
    {
        var template = TemplateFactory.Blank("我的唛头");

        Assert.False(TemplateValidator.Validate(template).HasError());
        Assert.Equal(4, template.Elements.Count);
        Assert.Equal("我的唛头", template.Name);
        Assert.False(template.BuiltIn);
    }

    [Fact]
    public void BlankTemplateHonoursRequestedSize()
    {
        var template = TemplateFactory.Blank("小标签", 60, 40);

        Assert.Equal(60, template.WidthMm);
        Assert.Equal(40, template.HeightMm);
        Assert.False(TemplateValidator.Validate(template).HasError());
        foreach (var element in template.Elements)
        {
            var box = EditGeometry.BoxOf(element);
            Assert.True(box.X + box.Width <= 60 + TemplateValidator.ToleranceMm, $"{element.Kind} 右侧越界");
            Assert.True(box.Y + box.Height <= 40 + TemplateValidator.ToleranceMm, $"{element.Kind} 下方越界");
        }
    }

    [Fact]
    public void CopyOfBuiltInIsAnIndependentUserTemplate()
    {
        var source = BuiltInTemplates.All.First();
        var copy = TemplateFactory.CopyOf(source, "客户 A 专用");

        Assert.False(copy.BuiltIn);
        Assert.StartsWith("user.", copy.Id);
        Assert.NotSame(source.Elements[0], copy.Elements[0]);

        copy.Elements[0].X += 5;
        Assert.NotEqual(copy.Elements[0].X, source.Elements[0].X);
    }

    [Fact]
    public void NewElementsCarryPrintableDefaults()
    {
        Assert.False(TemplateValidator.Validate(new LabelTemplate
        {
            Elements =
            {
                TemplateFactory.NewText("{{GrossWeight}}", 4, 4, 30, 6),
                TemplateFactory.NewLine(4, 12, 60, 12),
                TemplateFactory.NewRect(4, 16, 30, 10),
            },
        }).HasError());

        Assert.Equal(0.35, TemplateFactory.NewLine(0, 0, 10, 0).ThicknessMm, 6);
        Assert.True(TemplateFactory.NewRect(0, 0, 0.1, 0.1).Width >= EditGeometry.MinSideMm);
    }

    [Fact]
    public void AddedElementAvoidsLandingOnTopOfOthers()
    {
        var template = Page();
        template.Elements.Add(Box(4, 4, 40, 10));

        var index = TemplateFactory.AddElement(template, TemplateFactory.NewText("{{Origin}}", 4, 4, 30, 6), 4, 4);

        Assert.NotNull(index);
        var added = EditGeometry.BoxOf(template.Elements[index!.Value]);
        var occupied = EditGeometry.BoxOf(template.Elements[0]);
        Assert.False(TemplateFactory.Overlaps(occupied, added), "新建元素压住了已有的，用户会以为没加上");
    }

    [Fact]
    public void AddedElementStaysOnThePaper()
    {
        var template = Page(width: 40, height: 30);

        var index = TemplateFactory.AddElement(template, TemplateFactory.NewRect(0, 0, 30, 20), 0, 0);

        var box = EditGeometry.BoxOf(template.Elements[index!.Value]);
        Assert.True(box.X + box.Width <= 40 + TemplateValidator.ToleranceMm);
        Assert.True(box.Y + box.Height <= 30 + TemplateValidator.ToleranceMm);
    }

    [Fact]
    public void FullPaperRefusesMoreElements()
    {
        var template = Page();
        template.Elements.Add(Box(0, 0, 100, 80));

        Assert.Null(TemplateFactory.AddElement(template, TemplateFactory.NewRect(0, 0, 30, 20), 4, 4));
    }

    [Fact]
    public void ElementCountCapIsEnforced()
    {
        var template = Page();
        for (var i = 0; i < TemplateValidator.MaxElements; i++)
        {
            template.Elements.Add(Box(0, 0, 2, 2));
        }

        Assert.Null(TemplateFactory.AddElement(template, TemplateFactory.NewRect(0, 0, 2, 2), 0, 0));
    }

    [Fact]
    public void DuplicateIsOffsetAndDetached()
    {
        var template = Page();
        var source = Box(10, 10, 20, 8);
        source.Text = "{{PoNumber}}";
        template.Elements.Add(source);

        var copy = TemplateFactory.Duplicate(template, 0, 2);

        Assert.Equal(12, copy.X, 6);
        Assert.NotSame(template.Elements[0], copy);
        copy.X = 99;
        Assert.Equal(10, template.Elements[0].X, 6);
    }

    [Fact]
    public void FieldTextCarriesThePlaceholderSyntax()
    {
        var template = Page();

        var index = TemplateFactory.AddFieldText(template, "NetWeight");

        Assert.NotNull(index);
        Assert.Equal("{{NetWeight}}", template.Elements[index!.Value].Text);
        Assert.False(TemplateValidator.Validate(template).HasError());
    }

    // ---------- 模板库配合编辑器的两条新入口 ----------

    [Fact]
    public void FindFileLocatesSavedUserTemplateOnly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "labelgou-m4-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new TemplateStore(directory);
            Assert.Null(store.FindFileFor(BuiltInTemplates.IdStandard));

            var template = TemplateFactory.Blank("找得到的模板");
            var (saved, fileName, _) = store.Save(template);

            Assert.True(saved);
            var found = store.FindFileFor(template.Id);
            Assert.NotNull(found);
            Assert.Equal(fileName, Path.GetFileName(found!));
        }
        finally
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExportedJsonReadsBackIdentically()
    {
        var directory = Path.Combine(Path.GetTempPath(), "labelgou-m4-" + Guid.NewGuid().ToString("N")[..8]);
        var source = TemplateFactory.Blank("导出再导入");
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "export.json");
            new TemplateStore(directory).ExportFile(source, path);

            var (readBack, issues) = new TemplateStore(directory).ReadFile(path);

            Assert.False(issues.HasError());
            Assert.NotNull(readBack);
            Assert.Equal(source.Elements.Count, readBack!.Elements.Count);
            Assert.Equal(source.Elements[0].X, readBack.Elements[0].X, 6);
            Assert.False(readBack.BuiltIn);
        }
        finally
        {
            if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, recursive: true);
        }
    }
}
