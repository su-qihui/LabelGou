using System.Linq;
using LabelGou.Core.Impos;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 店里那四档开法的纸规与配套模板（用户 2026-09-09 口述定案）。
/// <para>原话：「<strong>现在的内置模版几乎全都不符逻辑（而且一般用的是一开四：28*20 的 14*10 的 4 个标签，
/// 一开八 28*20 的 10*7 的 8 个标签，大开二 28*20 的 20*14 的 2 标签，小开二 16*24 的 16*12 的 2 个标签）</strong>」。</para>
/// <para>这里钉三件事：① 每档实际能排出用户说的那个枚数（不是名字对、几何错）；
/// ② 每档都有一份同名同尺寸的配套模板（③ 步与 ④ 步能一眼对上）；
/// ③ M1 那两套一家样张都对不上的分格模板不再出现在下拉里。</para>
/// </summary>
public class CutSpecFamilyTests
{
    /// <summary>一开四：280×200 纸上 2×2 = 4 枚 140×100，不用旋转。</summary>
    [Fact]
    public void 一开四铺满四枚140x100()
    {
        var spec = BuiltInSheetSpecs.Cut4_280x200();
        var plan = ImpositionEngine.Build(spec, 140, 100, 1);

        Assert.Equal(4, plan.PerPage);
        Assert.False(plan.Grid.Rotated);
        Assert.True(plan.ErrorCount == 0, string.Join(" | ", plan.Issues.ErrorMessages()));
        Assert.Equal(100d, plan.UtilizationPercent, 1);
    }

    /// <summary>
    /// 一开八：280×200 纸上 8 枚 100×70。
    /// <para>正着放只能 4 枚，必须转 90° 摆成 4 列 × 2 行才拿得到 8 枚（8×7000 = 整张面积，一分不剩）。
    /// 旋转的是摆位不是内容——裁下来贴到箱子上字仍是正的，所以这条不该被当成错。</para>
    /// </summary>
    [Fact]
    public void 一开八转90度后铺满八枚100x70()
    {
        var spec = BuiltInSheetSpecs.Cut8_280x200();
        Assert.True(spec.AllowRotate, "一开八不开允许旋转就只能排出 4 枚，与用户说的 8 枚对不上");

        var plan = ImpositionEngine.Build(spec, 100, 70, 1);

        Assert.Equal(8, plan.PerPage);
        Assert.Equal(4, plan.Grid.Columns);
        Assert.Equal(2, plan.Grid.Rows);
        Assert.True(plan.Grid.Rotated);
        Assert.True(plan.ErrorCount == 0, string.Join(" | ", plan.Issues.ErrorMessages()));
        Assert.Equal(100d, plan.UtilizationPercent, 1);
    }

    /// <summary>大开二：280×200 纸上 2 枚 200×140（同样要转 90° 才放得下两枚）。</summary>
    [Fact]
    public void 大开二转90度后铺满两枚200x140()
    {
        var spec = BuiltInSheetSpecs.Big2_280x200();
        var plan = ImpositionEngine.Build(spec, 200, 140, 1);

        Assert.Equal(2, plan.PerPage);
        Assert.True(plan.Grid.Rotated);
        Assert.True(plan.ErrorCount == 0, string.Join(" | ", plan.Issues.ErrorMessages()));
        Assert.Equal(100d, plan.UtilizationPercent, 1);
    }

    /// <summary>小开二：160×240 纸上 2 枚 160×120，竖着叠两枚，不用旋转。</summary>
    [Fact]
    public void 小开二竖着叠两枚160x120()
    {
        var spec = BuiltInSheetSpecs.Small2_160x240();
        var plan = ImpositionEngine.Build(spec, 160, 120, 1);

        Assert.Equal(2, plan.PerPage);
        Assert.False(plan.Grid.Rotated);
        Assert.True(plan.ErrorCount == 0, string.Join(" | ", plan.Issues.ErrorMessages()));
        Assert.Equal(100d, plan.UtilizationPercent, 1);
    }

    /// <summary>
    /// 四档都是「一枚唛头独占一页、页内铺满全同」——用户 2026-09-08 的红框定案对每一档同样成立，
    /// 不是只给一开四开的特例。
    /// </summary>
    [Fact]
    public void 四档开法都是一枚独占一页铺满全同()
    {
        var pairs = new[]
        {
            (BuiltInSheetSpecs.Cut4_280x200(), 140, 100, 4),
            (BuiltInSheetSpecs.Cut8_280x200(), 100, 70, 8),
            (BuiltInSheetSpecs.Big2_280x200(), 200, 140, 2),
            (BuiltInSheetSpecs.Small2_160x240(), 160, 120, 2),
        };

        foreach (var (spec, w, h, perPage) in pairs)
        {
            Assert.True(spec.RepeatSameLabelPerPage, $"{spec.Name} 默认就该是一页同一枚铺满");
            var plan = ImpositionEngine.Build(spec, w, h, 3);
            Assert.True(plan.OneLabelPerPage, spec.Name);
            // 3 枚不同的唛头 → 3 页，每页铺满 perPage 份全同 = 上纸 3×perPage 枚
            Assert.Equal(3, plan.PageCount);
            Assert.Equal(3 * perPage, plan.PhysicalLabelCount);
            Assert.All(plan.PlacementsOnPage(1), p => Assert.Equal(1, p.LabelIndex));
        }
    }

    /// <summary>每一档都有同名同尺寸的配套模板：③ 步选的那份与 ④ 步那张纸必须一眼对得上。</summary>
    [Fact]
    public void 每档开法都有配套模板且尺寸一致()
    {
        var expected = new[]
        {
            (SheetId: BuiltInSheetSpecs.IdCut4_280x200, TemplateId: BuiltInTemplates.IdRowsFour, W: 140d, H: 100d),
            (SheetId: BuiltInSheetSpecs.IdCut8_280x200, TemplateId: BuiltInTemplates.IdRowsEight, W: 100d, H: 70d),
            (SheetId: BuiltInSheetSpecs.IdBig2_280x200, TemplateId: BuiltInTemplates.IdRowsBig2Four, W: 200d, H: 140d),
            (SheetId: BuiltInSheetSpecs.IdSmall2_160x240, TemplateId: BuiltInTemplates.IdRowsSmall2Four, W: 160d, H: 120d),
        };

        foreach (var (sheetId, templateId, w, h) in expected)
        {
            var spec = BuiltInSheetSpecs.GetById(sheetId);
            Assert.NotNull(spec);
            var template = BuiltInTemplates.GetById(templateId);
            Assert.NotNull(template);
            Assert.Contains(template!.Id, BuiltInTemplates.All.Select(t => t.Id));   // 真的在下拉里
            Assert.Equal(w, template.WidthMm, 6);
            Assert.Equal(h, template.HeightMm, 6);
            // 纸规的刀模尺寸 = 模板尺寸：不一致就会撞上那条「拼版将按模板尺寸落位」的告警
            Assert.Equal(w, spec!.LabelWidthMm, 6);
            Assert.Equal(h, spec.LabelHeightMm, 6);
        }
    }

    /// <summary>配套模板都是同一套四行版式（顶部客户名 + Item no / QTY / Ctns），不是各抄一份。</summary>
    [Fact]
    public void 四档配套模板都是同一套四行版式()
    {
        foreach (var id in new[]
                 {
                     BuiltInTemplates.IdRowsFour, BuiltInTemplates.IdRowsEight,
                     BuiltInTemplates.IdRowsBig2Four, BuiltInTemplates.IdRowsSmall2Four,
                 })
        {
            var texts = BuiltInTemplates.GetById(id)!.Elements
                .Where(e => e.Kind == ElementKind.Text)
                .Select(e => e.Text)
                .ToList();

            Assert.Equal(4, texts.Count);
            Assert.Contains("{{Consignee}}", texts);
            Assert.Contains(texts, t => t!.Contains("Item no：{{ItemNo}}", System.StringComparison.Ordinal));
            Assert.Contains(texts, t => t!.Contains("QTY：{{Quantity}} pcs", System.StringComparison.Ordinal));
            Assert.Contains(texts, t => t!.Contains("Ctns：{{col:本行箱数}}件", System.StringComparison.Ordinal));

            // 三行明细同字号（用户圈过的「字体大小不统一」就是这条）
            var detail = BuiltInTemplates.GetById(id)!.Elements
                .Where(e => e.Kind == ElementKind.Text && e.Text != "{{Consignee}}")
                .Select(e => e.FontSizePt)
                .Distinct()
                .ToList();
            Assert.Single(detail);
        }
    }

    /// <summary>
    /// M1 那两套分格模板退出下拉。用户说「内置模板几乎全都不符逻辑」，一家真样张都不是这个形状；
    /// 但方法留着、<c>GetById</c> 仍可解析（老方案 JSON 里存着这些 id，不能解析成 null）。
    /// </summary>
    [Fact]
    public void 旧式分格模板不再进下拉但仍可解析()
    {
        var ids = BuiltInTemplates.All.Select(t => t.Id).ToList();

        Assert.DoesNotContain(BuiltInTemplates.IdCompact, ids);
        Assert.DoesNotContain(BuiltInTemplates.IdBilingual, ids);
        Assert.NotNull(BuiltInTemplates.GetById(BuiltInTemplates.IdCompact));
        Assert.NotNull(BuiltInTemplates.GetById(BuiltInTemplates.IdBilingual));
        // 九字段那套留着：它是「自动挑模板按命中率」那条回归测试的对手
        Assert.Contains(BuiltInTemplates.IdStandard, ids);
    }

    /// <summary>四档开法排在纸规下拉最前：那是店里天天用的，不该藏在 A4 后面。</summary>
    [Fact]
    public void 纸规清单把四档开法排在最前()
    {
        var ids = BuiltInSheetSpecs.All().Select(s => s.Id).ToList();

        Assert.Equal(
            new[]
            {
                BuiltInSheetSpecs.IdCut4_280x200, BuiltInSheetSpecs.IdCut8_280x200,
                BuiltInSheetSpecs.IdBig2_280x200, BuiltInSheetSpecs.IdSmall2_160x240,
            },
            ids.Take(4).ToArray());
    }
}
