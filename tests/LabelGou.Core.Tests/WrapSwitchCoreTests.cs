using System.IO;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 85 棒①：「在哪儿断行」与「缩到多宽」拆成两件事之后，缺省值与落盘各钉一头。
/// <para>用户拿 ④ 步「全部行」里那三格重影逼出来的（长货号折成两行、压在下一格行带上），
/// 他要"即使超出也不折行"，但同时拍了<strong>「已有方案不更改」</strong>——
/// 所以这条开关必须是<strong>缺字段 = 允许折行 = 逐字旧行为</strong>，只有新出的行式骨架与 AI 版式写 false。
/// 下面四条各守一处：新出的版式默认关、老文件读进来行为不变、开关只在关掉时上盘、
/// 以及"折行宽度 0"那第三档不许被这次改动带跑。</para>
/// </summary>
public sealed class WrapSwitchCoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "labelgou-wrap-" + Guid.NewGuid().ToString("N")[..6]);

    public WrapSwitchCoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 临时目录留给人看一次就够了 */ }
    }

    private static LabelTemplate FourRows()
    {
        var spec = new RowLayoutSpec { WidthMm = 140, HeightMm = 100, PaddingMm = 5, GapMm = 2 };
        spec.Rows.Add(new RowSpec { Content = "ITEM NO: {{ItemNo}}", SizePt = 30 });
        spec.Rows.Add(new RowSpec { Content = "QTY: {{Quantity}} PCS", SizePt = 38 });
        spec.Rows.Add(new RowSpec { Content = "{{Consignee}}", Weight = 1.6, Stretch = true });
        return spec.Build()!;
    }

    /// <summary>
    /// 行式骨架/AI 版式的每一行：<strong>折行关掉，但折行宽度留着</strong>——留着才有"多宽算缩字"的尺子。
    /// 只钉这一条不够（关掉折行顺手把宽度清 0 也能过），所以两件事分开断言。
    /// </summary>
    [Fact]
    public void ARowStyleTemplateTurnsWrappingOffButKeepsItsShrinkWidth()
    {
        var rows = FourRows().Elements.Where(e => e.Kind == ElementKind.Text).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.False(row.AllowWrap ?? true, "行式骨架没把折行关掉（AllowWrap 该是 false）"));
        Assert.All(rows, row => Assert.True(row.WrapWidthMm > 0,
            "折行宽度被一起清成 0 = 这行连缩字的机会都没了"));
        Assert.All(rows, row => Assert.False(row.Wraps, "AllowWrap=false 却仍判成折行：判据没并到一处"));
    }

    /// <summary>老文件（没有这个字段）读进来必须照旧折行——这是「已有方案不更改」那条拍板的机器形状。</summary>
    [Fact]
    public void AnOldFileWithoutTheFieldStillWraps()
    {
        var template = FourRows();
        Assert.All(template.Elements.OfType<TemplateElement>(), e => e.AllowWrap = null);
        var json = TemplateStore.ToJson(template)
            .Replace("\"allowWrap\": false,", "", StringComparison.Ordinal);
        Assert.DoesNotContain("allowWrap", json, StringComparison.Ordinal);     // 夹具自证：真的把字段拿掉了

        var path = Path.Combine(_dir, "old.json");
        File.WriteAllText(path, json);
        var (read, issues) = new TemplateStore(_dir).ReadFile(path);
        Assert.NotNull(read);
        Assert.False(issues.HasError(), string.Join(" | ", issues.ErrorMessages()));
        var rows = read!.Elements.Where(e => e.Kind == ElementKind.Text).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, e => Assert.Null(e.AllowWrap));
        Assert.All(rows, e => Assert.True(e.WrapWidthMm > 0 && e.Wraps,
            "缺字段的老模板被这次改动带跑了：它本该继续折行"));
    }

    /// <summary>
    /// 开关<strong>只在关掉时上盘</strong>：属性面板把"允许折行"勾回来时存的是 <c>null</c>（撤掉字段）而不是 <c>true</c>，
    /// 这样这只元素与 v12 老文件逐字同形（与 <c>stroked</c> 同一条纪律，§五-62 那一族）。
    /// 万一哪天读进一份显式写着 <c>true</c> 的文件，行为也必须与缺字段一模一样——所以两种写法都测。
    /// </summary>
    [Fact]
    public void OnlyASwitchedOffWrapGoesOnDisk()
    {
        var off = FourRows();
        var offJson = TemplateStore.ToJson(off);
        Assert.Contains("\"allowWrap\": false", offJson, StringComparison.Ordinal);

        foreach (var element in off.Elements.OfType<TemplateElement>()) element.AllowWrap = null;
        Assert.DoesNotContain("allowWrap", TemplateStore.ToJson(off), StringComparison.Ordinal);

        foreach (var element in off.Elements.OfType<TemplateElement>()) element.AllowWrap = true;
        var explicitOn = TemplateStore.ToJson(off);

        foreach (var want in new bool?[] { null, true })
        {
            var json = want is null
                ? explicitOn.Replace("\"allowWrap\": true,", "", StringComparison.Ordinal)
                : explicitOn;
            var path = Path.Combine(_dir, $"on-{want}.json");
            File.WriteAllText(path, json);
            var (read, issues) = new TemplateStore(_dir).ReadFile(path);
            Assert.NotNull(read);
            Assert.False(issues.HasError(), string.Join(" | ", issues.ErrorMessages()));
            var how = want is null ? "缺字段" : "显式 true";
            Assert.All(read!.Elements.Where(e => e.Kind == ElementKind.Text),
                e => Assert.True(e.Wraps, $"{how} 却判成不折行"));
        }
    }

    /// <summary>
    /// 第三档不许被带跑：<strong>折行宽度 0</strong>（第 46 棒的默认）本来就是"不按宽度做任何事"，
    /// 这时 <c>AllowWrap</c> 填什么都不能凭空造出一个缩字目标来。
    /// </summary>
    [Fact]
    public void AZeroWidthStillMeansNoWidthRuleAtAll()
    {
        foreach (var allow in new bool?[] { null, true, false })
        {
            var element = new TemplateElement { Kind = ElementKind.Text, Text = "QTY", WrapWidthMm = 0, AllowWrap = allow };
            Assert.False(element.Wraps, $"折行宽度 0 却判成折行（AllowWrap={allow}）");
            Assert.True(element.NoWrap);
        }
    }
}
