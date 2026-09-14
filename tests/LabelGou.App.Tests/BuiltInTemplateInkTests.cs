using System.Text;
using LabelGou.App.Rendering;
using LabelGou.Core.Layout;
using LabelGou.Core.Marks;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 内置模板按**真数据**体检（第 64 棒①，用户：「把默认模板修一下，有些超出去或者有问题」）。
/// Core 量得了的那一半在 <c>BuiltInTemplateAuditTests</c>（越界/Error）已经干净；这一条补 Core 量不到的：
/// 文字按样例值排开之后**墨迹探出标签**（永不折行的行带）、以及校验器的警告清单。
/// 先当探针用：把每份模板的问题逐条列出来，修完这些判据就该全绿。
/// </summary>
public class BuiltInTemplateInkTests
{
    private static void OnSta(Action work)
        => LabelGou.App.Export.StaWorker.RunAsync((_, _) =>
        {
            work();
            return true;
        }, null, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void BuiltInTemplatesDoNotRunOffTheLabelWithSampleData() => OnSta(() =>
    {
            var bad = new List<string>();
            foreach (var template in BuiltInTemplates.All())
            {
                foreach (var issue in TemplateValidator.Validate(template)
                             .Where(i => i.Severity != IssueLevel.Info))
                    bad.Add($"「{template.Name}」[{issue.Severity}] {issue.Message}");

                var record = SampleFor(template, longValues: false);
                var layout = LayoutEngine.Build(template, record, new LayoutContext(1, 1, "体检.xlsx"));
                foreach (var (element, i) in template.Elements.Select((e, i) => (e, i)))
                {
                    if (element.Kind != ElementKind.Text || !element.Visible) continue;
                    var item = layout.Items.OfType<TextItem>().FirstOrDefault(t => ReferenceEquals(t.Source, element));
                    if (item is null) continue;
                    if (TextInkBox.Measure(item) is not { } ink) continue;
                    var over = Math.Max(Math.Max(ink.X + ink.Width - template.WidthMm, ink.Y + ink.Height - template.HeightMm),
                                        Math.Max(-ink.X, -ink.Y));
                    if (over > TemplateValidator.ToleranceMm)
                        bad.Add($"「{template.Name}」第 {i + 1} 个元素墨迹探出标签 {over:0.#} mm（{element.Text}，字号 {item.FontSizePt:0.#}pt）");
                }
            }
            Assert.True(bad.Count == 0, "内置模板体检出问题：\n" + string.Join("\n", bad));
    });

    /// <summary>
    /// 两份口径分开量：
    /// ① <b>标准样例</b>——就是他在预览里看到的那份数据，模板连这个都出纸就是模板的病；
    /// ② 长值——客户名/货号真会这么长（"箱数"这种数字列不给长值，那不算模板的账）。
    /// </summary>
    private static MarkRecord SampleFor(LabelTemplate template, bool longValues)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in template.Elements)
            foreach (var t in TemplateTokenizer.EnumerateTokens(e.Text ?? string.Empty)) tokens.Add(t);

        var builder = SampleRecords.StandardSample().ToBuilder();
        if (!longValues) return builder.Build();
        foreach (var token in tokens)
        {
            if (!token.StartsWith("col:", StringComparison.OrdinalIgnoreCase)) continue;
            var column = token[4..].Trim();
            if (column.Contains("箱数") || column.Contains("数量")) continue;         // 数字列不长，拿它测模板是冤枉
            builder.SetCustom("col:" + column, "GIRAR RETROEXCAVADORA 90/13B NR.240618-05");
        }
        return builder.Build();
    }
}
