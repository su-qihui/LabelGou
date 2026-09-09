using LabelGou.Core.Impos;
using LabelGou.Core.Templates;
using LabelGou.Core.Units;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// M7 · 行式版式骨架与真样张纸规。
/// <para>
/// 断言口径与模板层一致：<strong>毫米手算对拍</strong>，不用被测代码自己算出来的数自证。
/// 行带高度、字号这些数值还拿真样张（<c>labelgou-CL</c> 里 CDR 导出的 SVG）当参照。
/// </para>
/// </summary>
public class RowLayoutTests
{
    private const double Eps = 1e-6;

    [Fact]
    public void 内置模板清单每次给新实例_界面改不脏库()
    {
        // All() 与 GetById() 同一个理由:别让界面拿到共享可变定义(第 23 棒审计-17)。
        var first = BuiltInTemplates.All()[0];
        first.Name = "被我改掉了";

        var again = BuiltInTemplates.All().First(t => t.Id == first.Id);
        Assert.NotSame(first, again);
        Assert.NotEqual("被我改掉了", again.Name);
    }

    private static RowLayoutSpec FourRows()
    {
        var spec = new RowLayoutSpec { WidthMm = 140, HeightMm = 100, PaddingMm = 5, GapMm = 2 };
        spec.Rows.Add(new RowSpec { Content = "{{Consignee}}", Weight = 1.6, Stretch = true });
        spec.Rows.Add(new RowSpec { Content = "Item no：{{ItemNo}}", Stretch = true });
        spec.Rows.Add(new RowSpec { Content = "QTY：{{Quantity}} pcs", Stretch = true });
        spec.Rows.Add(new RowSpec { Content = "Ctns：{{CartonTotal}}件", Stretch = true });
        return spec;
    }

    [Fact]
    public void BandsSplitUsableHeightByWeightAndTouchNeitherEdge()
    {
        var template = FourRows().Build();
        Assert.NotNull(template);
        var rows = template!.Elements.Where(e => e.Kind == ElementKind.Text).ToList();
        Assert.Equal(4, rows.Count);

        // 可用高 = 100 - 2*5 - 3*2 = 84，权重和 4.6 → 每权重 18.2609mm
        var perWeight = (100 - 10 - 6) / 4.6;
        Assert.Equal(5, rows[0].Y, 6);
        Assert.Equal(perWeight * 1.6, rows[0].Height, 6);
        Assert.Equal(5 + perWeight * 1.6 + 2, rows[1].Y, 6);
        Assert.Equal(perWeight, rows[1].Height, 6);
        // 最后一行的下沿必须正好落在下留白上，不越界也不留空
        Assert.True(Math.Abs(rows[3].Y + rows[3].Height - (100 - 5)) < Eps,
            $"最后一行下沿 {rows[3].Y + rows[3].Height:0.####} 应等于 95");
    }

    [Fact]
    public void StretchedRowSizeMatchesWhatTheVendorActuallyUsed()
    {
        // 真样本参照：7.8金沐唛头.svg 里 BOLAROM 是 23.43mm 字高（≈66pt）、明细行 14.6mm（≈41pt）。
        // 骨架算出 61pt / 38pt——同一量级，说明"字高占行带 0.74"这个系数不是拍脑袋。
        var rows = FourRows().Build()!.Elements.Where(e => e.Kind == ElementKind.Text).ToList();
        Assert.Equal(Mm.MmToPoint((100 - 10 - 6) / 4.6 * 1.6 * RowLayoutSpec.StretchEmPerBand), rows[0].FontSizePt, 6);
        Assert.InRange(rows[0].FontSizePt, 55, 70);
        Assert.InRange(rows[1].FontSizePt, 33, 42);
    }

    [Fact]
    public void NonStretchedRowKeepsTheRequestedSizeAndCanWrap()
    {
        var spec = new RowLayoutSpec { WidthMm = 140, HeightMm = 100, PaddingMm = 5, GapMm = 2 };
        spec.Rows.Add(new RowSpec { Content = "{{Remarks}}", Stretch = false, SizePt = 9, Bold = false });
        var row = Assert.Single(spec.Build()!.Elements);
        Assert.Equal(9, row.FontSizePt, 6);
        Assert.False(row.Bold);
        Assert.Equal(3, row.MaxLines);
    }

    [Fact]
    public void StretchedRowsAreLockedToOneLineSoTheyNeverBurstTheBand()
    {
        var rows = FourRows().Build()!.Elements.Cast<TemplateElement>().ToList();
        Assert.All(rows, r => Assert.Equal(1, r.MaxLines));
        Assert.All(rows, r => Assert.True(r.ShrinkToFit, "值变长时必须能缩字号，不能靠换行挤出这条带"));
    }

    [Fact]
    public void EmptyOrStarvedLayoutRefusesToBuild()
    {
        Assert.Null(new RowLayoutSpec().Build());                                        // 一行都没有
        var starved = new RowLayoutSpec { WidthMm = 40, HeightMm = 20, PaddingMm = 9, GapMm = 4 };
        starved.Rows.Add(new RowSpec { Content = "A", Stretch = true });
        starved.Rows.Add(new RowSpec { Content = "B", Stretch = true });
        Assert.Null(starved.Build());                                                    // 留白+行距把版面吃光
    }

    [Fact]
    public void EveryBuiltInTemplateStillPassesValidationIncludingTheRowOnes()
    {
        foreach (var template in BuiltInTemplates.All())
        {
            var issues = TemplateValidator.Validate(template);
            Assert.False(issues.HasError(), $"{template.Id}: {string.Join(" | ", issues.ErrorMessages())}");
        }
        Assert.NotNull(BuiltInTemplates.GetById(BuiltInTemplates.IdRowsFour));
        Assert.NotNull(BuiltInTemplates.GetById(BuiltInTemplates.IdRowsBigTwo));
        Assert.True(BuiltInTemplates.GetById(BuiltInTemplates.IdRowsFour)!.BuiltIn);
    }

    [Fact]
    public void BuiltInTemplatesReturnFreshInstancesSoTheEditorCannotCorruptTheSeed()
    {
        var first = BuiltInTemplates.GetById(BuiltInTemplates.IdRowsFour)!;
        first.Elements.Clear();
        Assert.NotEmpty(BuiltInTemplates.GetById(BuiltInTemplates.IdRowsFour)!.Elements);
    }

    [Fact]
    public void CutFourSheetFitsExactlyFourLabelsOnOneSheet()
    {
        var spec = BuiltInSheetSpecs.Cut4_280x200();
        Assert.False(SheetSpecValidator.Validate(spec).HasError());
        Assert.Equal(280, spec.PaperWidthMm, 6);
        Assert.Equal(200, spec.PaperHeightMm, 6);
        Assert.Equal(2, spec.Columns);
        Assert.Equal(2, spec.Rows);
        Assert.False(spec.AllowRotate);

        // 4 枚 140×100 刚好铺满：宽 2*140 = 280、高 2*100 = 200，页边与间距都是 0
        Assert.Equal(280, 2 * spec.LabelWidthMm + spec.GutterXMm * (spec.Columns - 1)
            + spec.MarginLeftMm + spec.MarginRightMm, 6);
        Assert.Equal(200, 2 * spec.LabelHeightMm + spec.GutterYMm * (spec.Rows - 1)
            + spec.MarginTopMm + spec.MarginBottomMm, 6);

        Assert.NotNull(BuiltInSheetSpecs.GetById(BuiltInSheetSpecs.IdCut4_280x200));
        Assert.Contains(BuiltInSheetSpecs.All(), s => s.Id == BuiltInSheetSpecs.IdCut4_280x200);
    }
}
