using System.IO;
using LabelGou.Core.Editing;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 49 棒：线条升级成贝塞尔曲线，地基是这一层。
/// <para>
/// 三条要钉死的：① <strong>没画曲线 = 逐字旧行为</strong>（三个字段一个都不写，老模板文件不用更新）；
/// ② <strong>端点只有一个出处</strong>（起终点仍住在 X/Y 与 X2/Y2，节点表只存中间的，拖完不会两处不一致）；
/// ③ <strong>量的是弧本身</strong>——外接框与命中都不许拿"两端连一条弦"或"控制点围的框"糊弄，
/// 否则选择框虚胖、点得中看不见墨的地方、越界校验还漏报。
/// </para>
/// </summary>
public class CurveGeometryTests
{
    private static TemplateElement Line(double x1, double y1, double x2, double y2) => new()
    {
        Kind = ElementKind.Line, X = x1, Y = y1, X2 = x2, Y2 = y2,
        Width = Math.Abs(x2 - x1), Height = Math.Abs(y2 - y1),
    };

    private static LabelTemplate Wrap(params TemplateElement[] elements)
    {
        var template = new LabelTemplate
        {
            Id = "user.curve", Name = "曲线测试", WidthMm = 100, HeightMm = 60, PaddingMm = 2, BorderMm = 0,
        };
        template.Elements.AddRange(elements);
        return template;
    }

    /// <summary>按库内口径写出去再读回来（序列化与反序列化都过，才叫"存住了"）。</summary>
    private static LabelTemplate RoundTrip(LabelTemplate template)
    {
        var dir = Path.Combine(Path.GetTempPath(), "labelgou-curve-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "case.json");
        File.WriteAllText(path, TemplateStore.ToJson(template));
        var (read, issues) = new TemplateStore(dir).ReadFile(path);
        Assert.NotNull(read);
        Assert.False(issues.HasError(), string.Join(" | ", issues.ErrorMessages()));
        return read!;
    }

    /// <summary>一条向下鼓的对称弧：两端都在 y=30，最低点正好在 (50,15)。</summary>
    private static TemplateElement ArcDown()
    {
        var element = Line(10, 30, 90, 30);
        CurveGeometry.ApplyNodes(element, new[]
        {
            new CurveNode(10, 30, 0, 0, 20, -20),
            new CurveNode(90, 30, -20, -20, 0, 0),
        });
        return element;
    }

    // ---------- ① 没画曲线 = 旧行为 ----------

    [Fact]
    public void APlainLineCarriesNoCurveFieldsAtAll()
    {
        var element = Line(4, 8, 60, 8);
        Assert.False(CurveGeometry.IsCurved(element));

        var json = TemplateStore.ToJson(Wrap(element));
        Assert.DoesNotContain("\"nodes\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"startOut\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"endIn\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DrawingAStraightLineWithTheToolStillWritesTheOldShape()
    {
        // 曲线工具拖一条直线（两端柄归零、无中间节点）→ ApplyNodes 必须把三个字段清空，
        // 不是"存一份长度为 0 的柄"：同一个动作在新老文件里只能长一个样子。
        var element = Line(10, 10, 70, 30);
        CurveGeometry.ApplyNodes(element, new[]
        {
            new CurveNode(10, 10, 0, 0, 0, 0),
            new CurveNode(70, 30, 0, 0, 0, 0),
        });
        Assert.Null(element.Nodes);
        Assert.Null(element.StartOut);
        Assert.Null(element.EndIn);
        Assert.Equal((10d, 10d, 70d, 30d), (element.X, element.Y, element.X2, element.Y2));
    }

    [Fact]
    public void SegmentsOfAPlainLineIsOneStraightSegmentMatchingTheEndpoints()
    {
        var s = Assert.Single(CurveGeometry.Segments(Line(4, 8, 60, 20)));
        Assert.True(s.IsStraight);
        Assert.Equal((4d, 8d, 60d, 20d), (s.X1, s.Y1, s.X2, s.Y2));
    }

    // ---------- ② 端点只有一个出处 ----------

    [Fact]
    public void EndpointsLiveOnlyInTheOldFieldsAndRoundTripThroughTheNodeList()
    {
        var element = Line(5, 5, 85, 40);
        CurveGeometry.ApplyNodes(element, new[]
        {
            new CurveNode(5, 5, 0, 0, 12, -8),
            new CurveNode(40, 12, -6, 9, 10, -10),
            new CurveNode(85, 40, -14, 4, 0, 0),
        });

        Assert.Equal(5, element.X);
        Assert.Equal(40, element.Y2);                       // 终点没被节点表带跑
        Assert.Equal(12, element.StartOut!.DX);
        Assert.Single(element.Nodes!);                      // 只有中间那个进表
        Assert.Equal(40d, element.Nodes![0].X);

        var back = CurveGeometry.NodesOf(element);
        Assert.Equal(3, back.Count);
        Assert.Equal((5d, 5d), (back[0].X, back[0].Y));
        Assert.Equal((85d, 40d), (back[^1].X, back[^1].Y));
        Assert.Equal((10d, -10d), (back[1].OutX, back[1].OutY));
    }

    [Fact]
    public void CurveFieldsSurviveTheTemplateFileByteForByte()
    {
        var element = Line(5, 5, 85, 40);
        CurveGeometry.ApplyNodes(element, new[]
        {
            new CurveNode(5, 5, 0, 0, 12, -8),
            new CurveNode(40, 12, -6, 9, 10, -10),
            new CurveNode(85, 40, -14, 4, 0, 0),
        });
        var read = Assert.Single(RoundTrip(Wrap(element)).Elements);

        Assert.Equal(element.StartOut, read.StartOut);
        Assert.Equal(element.EndIn, read.EndIn);
        Assert.Equal(element.Nodes, read.Nodes);
    }

    [Fact]
    public void OneSidedHandlesAreKeptInsteadOfBeingInventedFromTheOtherSide()
    {
        // 47 棒那条"两端并存、谁都不许由另一侧现算"的几何版：起点只有出柄（第一个点当然如此），
        // 读回来不能给它编一根进柄。
        var element = Line(5, 5, 85, 40);
        CurveGeometry.ApplyNodes(element, new[]
        {
            new CurveNode(5, 5, 0, 0, 20, 0),
            new CurveNode(85, 40, 0, 0, 0, 0),
        });
        var read = Assert.Single(RoundTrip(Wrap(element)).Elements);
        var start = CurveGeometry.NodesOf(read)[0];
        Assert.Equal(0, start.InX);
        Assert.Equal(20, start.OutX);
    }

    // ---------- ③ 量的是弧 ----------

    [Fact]
    public void BoundsFollowTheArcNotTheChordOrTheControlPoints()
    {
        // 拿弦量会得到零高（两端都在 y=30），拿控制点量会得到 y=10（虚胖一倍），
        // 只有解导数根才量到真正的最低点 (50,15)。
        var box = CurveGeometry.BoundsMm(ArcDown());
        Assert.Equal(15, box.Y, 1);
        Assert.Equal(10, box.X, 1);
        Assert.Equal(90, box.X + box.Width, 1);
    }

    [Fact]
    public void HitTestFollowsTheArcAndIgnoresTheChord()
    {
        var arc = ArcDown();
        Assert.True(CurveGeometry.DistanceMm(arc, 50, 15) < 0.6,
            $"弧身上那一点距离 {CurveGeometry.DistanceMm(arc, 50, 15):0.##} mm——点不中就是命中没跟着弧走");
        Assert.True(CurveGeometry.DistanceMm(arc, 50, 30) > 6,
            "弦的中点还判得中 = 拿两端连线糊弄");
        Assert.True(EditGeometry.HitTest(arc, 50, 15));
        Assert.False(EditGeometry.HitTest(arc, 50, 30));
    }

    [Fact]
    public void SmoothNodesMirrorAndCornersDoNot()
    {
        Assert.True(CurveGeometry.IsSmooth(new CurveNode(10, 10, -8, 0, 8, 0)));         // 两柄成一直线
        Assert.False(CurveGeometry.IsSmooth(new CurveNode(10, 10, -8, 0, 0, 8)));        // 直角：尖角节点
        Assert.False(CurveGeometry.IsSmooth(new CurveNode(10, 10, 0, 0, 8, 0)));         // 只有一根柄
        Assert.False(CurveGeometry.IsSmooth(new CurveNode(10, 10, 8, 0, 8, 0)));         // 同向：两柄在同一侧
    }

    [Fact]
    public void NodeEditsMoveOnlyWhatTheySayTheyMove()
    {
        var element = Line(10, 10, 90, 10);
        CurveGeometry.ApplyNodes(element, new[]
        {
            new CurveNode(10, 10, 0, 0, 0, 0),
            new CurveNode(50, 30, -6, 0, 6, 0),
            new CurveNode(90, 10, 0, 0, 0, 0),
        });

        CurveGeometry.MoveNode(element, 1, 60, 40);
        Assert.Equal(60, element.Nodes![0].X);
        Assert.Equal(40, element.Nodes[0].Y);
        Assert.Equal((10d, 10d), (element.X, element.Y));          // 两端没被带着走
        Assert.Equal((90d, 10d), (element.X2, element.Y2));
        Assert.Equal((-6d, 0d), (element.Nodes[0].InX, element.Nodes[0].InY));   // 柄跟着平移，弯度不变

        Assert.False(CurveGeometry.RemoveNode(element, 0));        // 起点不许删
        Assert.False(CurveGeometry.RemoveNode(element, 2));        // 终点也不许删
        Assert.True(CurveGeometry.RemoveNode(element, 1));
        Assert.Null(element.Nodes);                                // 中间点没了 → 回到两点直线，字段清空
    }

    [Fact]
    public void StraightenKeepsTheNodesAndKillsTheBend()
    {
        var element = Line(10, 10, 90, 10);
        CurveGeometry.ApplyNodes(element, new[]
        {
            new CurveNode(10, 10, 0, 0, 20, -20),
            new CurveNode(50, 30, -6, 0, 6, 0),
            new CurveNode(90, 10, -20, -20, 0, 0),
        });
        CurveGeometry.Straighten(element);

        Assert.Equal(3, CurveGeometry.NodesOf(element).Count);     // 节点还在
        Assert.All(CurveGeometry.Segments(element), s => Assert.True(s.IsStraight));
        Assert.NotNull(element.Nodes);                             // 中间节点还在表里，只是都不弯了
    }

    [Fact]
    public void MovingTheElementMovesTheArcWithIt()
    {
        var element = Line(10, 10, 90, 10);
        CurveGeometry.ApplyNodes(element, new[]
        {
            new CurveNode(10, 10, 0, 0, 0, 0),
            new CurveNode(50, 30, -6, 0, 6, 0),
            new CurveNode(90, 10, 0, 0, 0, 0),
        });
        var template = Wrap(element);
        EditGeometry.MoveBy(template, 0, 5, -3);

        Assert.Equal(15, element.X);
        Assert.Equal(55, element.Nodes![0].X);                     // 整条一起挪，不是只有两端动
        Assert.Equal(27, element.Nodes[0].Y);
        Assert.Equal((-6d, 0d), (element.Nodes[0].InX, element.Nodes[0].InY));   // 柄是相对量，跟着走不该改
    }

    [Fact]
    public void AbsurdNodeListsAreRefusedBeforeTheyReachThePaper()
    {
        var tooMany = Line(10, 10, 90, 10);
        tooMany.Nodes = Enumerable.Range(0, CurveGeometry.MaxNodes + 5)
            .Select(i => new CurveNode(20 + i % 60, 20, 0, 0, 0, 0)).ToList();
        Assert.Contains(TemplateValidator.Validate(Wrap(tooMany)).ErrorMessages(), m => m.Contains("超过上限"));

        var nan = Line(10, 10, 90, 10);
        nan.Nodes = new List<CurveNode> { new(double.NaN, 20, 0, 0, 0, 0) };
        Assert.Contains(TemplateValidator.Validate(Wrap(nan)).ErrorMessages(), m => m.Contains("有效数字"));
    }

    [Fact]
    public void AnArcBulgingOffThePaperIsCaughtEvenThoughBothEndsAreInside()
    {
        // 端点都在纸内、弧身鼓出纸外，是曲线新引入的那类越界：只量两端就会漏。
        var element = Line(40, 55, 60, 55);
        CurveGeometry.ApplyNodes(element, new[]
        {
            new CurveNode(40, 55, 0, 0, -20, 20),
            new CurveNode(60, 55, 20, 20, 0, 0),
        });
        Assert.Contains(TemplateValidator.Validate(Wrap(element)).ErrorMessages(), m => m.Contains("探出标签"));
    }
}
