using System.IO;
using System.Text;
using LabelGou.App.Export;
using LabelGou.App.Services;
using LabelGou.App.ViewModels;
using LabelGou.Core.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 「AI 得先看见整张表」这条链在主界面那一侧的钉子（M7 第 15 棒）。
/// <para>用户 2026-09-08 的口径：先做了自动绑定，AI 收到的信息就乱了；就算把正确排版给它，
/// 它也只会按我们连上的那几列来。所以这里钉的不是模型有多聪明，而是<strong>我们递了什么</strong>：
/// 没连上的列要一起递过去，而「一个字段都没连上」不能再当成拒发请求的理由（TOP 那张表就是这个形状）。</para>
/// </summary>
public sealed class AiSeesWholeTableTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-portrait");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    private string WriteCsv(string name, params string[] headers)
    {
        var path = Path.Combine(_dir, name);
        var text = string.Join(",", headers) + Environment.NewLine +
                   "QI YUE: AJ7-QI YUE: AJ9,olu830-35,5" + Environment.NewLine +
                   "QI YUE: AJ10,olu830-70,3" + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void 没导表时画像为空不编一份出来()
    {
        var snapshot = OnSta(() => new MainViewModel(TestEnvironment.NewTempUiStateStore()).BuildTablePortrait());

        Assert.Null(snapshot);
    }

    [Fact]
    public void 导进来的表里没连上的列也进了画像()
    {
        var csv = WriteCsv("郑小姐形状.csv", "流水号", "货号 ITEM NO:", "件数 CTN");

        var snapshot = OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            vm.LoadSource(csv, null);                       // 导入时就已经自动绑过一轮
            return vm.BuildTablePortrait();
        });

        Assert.NotNull(snapshot);
        var 流水号 = Assert.Single(snapshot!.Value.Columns, c => c.Header == "流水号");
        Assert.True(string.IsNullOrEmpty(流水号.BoundField), $"流水号本来就没在 19 个字段里，画像却把它连上了：{流水号.BoundField}");
        Assert.Contains("流水号", snapshot.Value.Portrait);
        Assert.Contains("没连到任何字段", snapshot.Value.Portrait);
        Assert.Contains("QI YUE: AJ7-QI YUE: AJ9", snapshot.Value.Portrait);   // 原文样例，不是清洗后的值
    }

    [Fact]
    public void 一个字段都没连上但有整表画像时照样能问()
    {
        var 只有画像 = new AiLayoutContext(
            Array.Empty<(string, string, string)>(), 140, 100, null,
            Array.Empty<ColumnPortrait>(), "整张表 411 行 × 6 列：\n  - 第1列「11150588」：没连到任何字段");
        var 什么都没有 = new AiLayoutContext(
            Array.Empty<(string, string, string)>(), 140, 100, null);
        var 只有字段 = new AiLayoutContext(
            new[] { ("ItemNo", "货号", "olu830-35") }, 140, 100, null);

        // TOP 那张 0 字段的表以前就是死在这一步：面板自己挡下请求，用户点四次只看到四行劝退话。
        Assert.True(只有画像.HasAnythingToAsk);
        Assert.False(什么都没有.HasAnythingToAsk);
        Assert.True(只有字段.HasAnythingToAsk);
    }

    [Fact]
    public void 面板把整表画像递进提示词而不是只递已连字段()
    {
        var csv = WriteCsv("三列表.csv", "流水号", "货号 ITEM NO:", "件数 CTN");

        var prompt = OnSta(() =>
        {
            var vm = new MainViewModel(TestEnvironment.NewTempUiStateStore());
            vm.LoadSource(csv, null);
            var portrait = vm.BuildTablePortrait()!.Value;
            var fields = vm.FieldRows.Where(r => r.Mapped)
                .Select(r => (r.FieldKey, r.DisplayName, r.SampleValue)).ToList();
            return RowLayoutPrompt.Build(fields, 140, 100, vm.StatusMessage, portrait.Portrait);
        });

        // 模型第一次知道"表里有这一列、而且我可以用 {{col:…}} 直取它"（归因文档 B-1 那半句便宜的修法）。
        Assert.Contains("整张表原样摊开（含没连上的列）", prompt);
        Assert.Contains("{{col:列名}}", prompt);
        Assert.Contains("流水号", prompt);
    }
}
