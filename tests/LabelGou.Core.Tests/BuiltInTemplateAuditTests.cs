using LabelGou.Core.Barcodes;
using LabelGou.Core.Editing;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 内置模板的体检（第 64 棒①，用户：「把默认模板修一下，有些超出去或者有问题」）。
/// <para>默认模板是他开箱第一眼看的东西，也是五家真样件对齐出来的基线——里面有元素探出标签、
/// 或者校验报 Error，等于出厂就带病。这条测试把 <see cref="BuiltInTemplates.All"/> 全过一遍：
/// 校验器不许报 Error，几何也不许探出标签（Core 量得了的那部分）。</para>
/// </summary>
public class BuiltInTemplateAuditTests
{
    [Fact]
    public void NoBuiltInTemplateHasErrorsOrSticksOutOfTheLabel()
    {
        var bad = new List<string>();
        foreach (var template in BuiltInTemplates.All())
        {
            var issues = TemplateValidator.Validate(template);
            foreach (var issue in issues.Where(i => i.Severity == IssueLevel.Error))
                bad.Add($"「{template.Name}」{issue.Message}");

            for (var i = 0; i < template.Elements.Count; i++)
            {
                var e = template.Elements[i];
                if (!e.Visible || e.ReferenceOnly) continue;
                var occ = EditGeometry.OccupiedBoundsOf(e);
                var over = Math.Max(Math.Max(occ.Right - template.WidthMm, occ.Bottom - template.HeightMm),
                                    Math.Max(-occ.X, -occ.Y));
                if (over > TemplateValidator.ToleranceMm)
                    bad.Add($"「{template.Name}」第 {i + 1} 个元素（{e.Kind}，内容 {Summarize(e)}）探出标签 {over:0.#} mm");
            }
        }

        Assert.True(bad.Count == 0, "内置模板出厂带病：\n" + string.Join("\n", bad));
    }

    /// <summary>
    /// 内置模板引用的每一个占位符，样例数据里都得有值（第 66 棒，用户：「Ctns 那行不见了，我叫你调好，
    /// 你留了一行只有框没有字的空墨迹」）。取不到值就命中「变量全空整条隐藏」——编辑器、缩略图、
    /// 导出预览、没编号时的主预览全都少这一行，而校验还会说"没问题"。
    /// </summary>
    [Fact]
    public void EveryBuiltInTemplateResolvesAllOfItsTokensAgainstTheSample()
    {
        var bad = new List<string>();
        foreach (var template in BuiltInTemplates.All())
        {
            var layout = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1, "样例.xlsx"));
            foreach (var token in layout.UnresolvedTokens)
            {
                var braces = "{{" + token + "}}";
                var who = template.Elements
                    .Select((e, i) => (e, i))
                    .Where(x => (x.e.Text ?? string.Empty).Contains(braces, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.i + 1).ToList();
                bad.Add($"「{template.Name}」{braces} 在样例里没有值（第 {string.Join("、", who)} 个元素）");
            }
        }
        Assert.True(bad.Count == 0, "这些内置模板的占位符拿样例数据取不到值，那一行会整条消失：\n" + string.Join("\n", bad));
    }

    private static string Summarize(TemplateElement e) =>
        e.Kind == ElementKind.Barcode ? $"条码 {e.Text}"
        : e.Kind == ElementKind.Image ? $"图片 {System.IO.Path.GetFileName(e.ImagePath ?? "")}"
        : (e.Text ?? "形状");
}
