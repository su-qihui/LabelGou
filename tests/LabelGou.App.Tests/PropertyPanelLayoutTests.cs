using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core.Colors;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 属性面板里<strong>任何一颗控件都不许被挤到面板右边外面去</strong>。
/// <para>起因：第 50 棒把填充、描边、宽度、四颗角全挤进一行，右边整截被面板裁掉——他截图里
/// 「描边」只剩一个"描"、右上/右下两颗和宽度框根本看不见，然后说「你把按钮边缘化了我看不到」。
/// 这类毛病编译、绑定、逻辑单测全都看不见，只有把窗口量一次布局才看得见，所以在这儿钉一条。</para>
/// <para>判据用"偏移 + 宽度 是否越出面板"，不用 DesiredSize：带省略号的文字会被压到刚好等于剩给它的宽度，
/// 那种量法分不清"挤掉了"和"文字缩了"。</para>
/// </summary>
public sealed class PropertyPanelLayoutTests
{
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void NoControlIsPushedOutOfThePropertyPanel() => OnSta(() =>
    {
        var template = new LabelTemplate
        {
            Id = "user.props", Name = "面板宽度看样", WidthMm = 100, HeightMm = 80, PaddingMm = 4, BorderMm = 0.5,
        };
        // 挑最挤的那只来量：两支墨都上满色（摘要那行最长）、圆角开着、四颗角与锁都在
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Rect, X = 20, Y = 18, Width = 40, Height = 30, ThicknessMm = 0.8,
            InkColor = LabelColor.FromCmyk(0, 100, 100, 100), FillColor = LabelColor.FromCmyk(100, 0, 100, 0),
            CornerRadiusMm = 6,
        });
        var vm = new TemplateEditorViewModel(template,
            new TemplateStore(Path.Combine(Path.GetTempPath(), "labelgou-props-" + Guid.NewGuid().ToString("N")[..6])));
        vm.SelectedRow = vm.Elements[0];

        var window = new TemplateEditorWindow(vm);
        // 不 Show 任何窗口（他此刻正开着应用），直接量窗口的内容根
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1180, 820));
        root.Arrange(new Rect(0, 0, 1180, 820));
        window.UpdateLayout();

        var panel = (FrameworkElement)window.FindName("PropsGrid")!;
        Assert.True(panel.ActualWidth > 60, $"面板没量到宽度（{panel.ActualWidth}）——这条测试就白跑了");

        var outOfBounds = new List<string>();
        Walk(panel, panel, outOfBounds);
        Assert.True(outOfBounds.Count == 0,
            $"面板宽 {panel.ActualWidth:0.#}，这些控件被挤到右边外面：\n" + string.Join("\n", outOfBounds));
        return true;
    });

    private static void Walk(FrameworkElement node, FrameworkElement grid, List<string> outOfBounds)
    {
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(node, i) is not FrameworkElement child) continue;
            Walk(child, grid, outOfBounds);
            if (child.ActualWidth <= 0) continue;

            var right = child.TranslatePoint(new Point(0, 0), grid).X + child.ActualWidth;
            if (right > grid.ActualWidth + 1.5)
            {
                var row = -1;
                for (FrameworkElement? up = child; up is not null; up = VisualTreeHelper.GetParent(up) as FrameworkElement)
                {
                    if (ReferenceEquals(VisualTreeHelper.GetParent(up), grid)) { row = Grid.GetRow(up); break; }
                }
                var text = child is System.Windows.Controls.TextBlock t ? " 「" + t.Text + "」" : "";
                outOfBounds.Add($"第 {row} 行 {child.GetType().Name}{text} 占到 {right:0.#}（面板只有 {grid.ActualWidth:0.#}）");
            }
        }
    }
}
