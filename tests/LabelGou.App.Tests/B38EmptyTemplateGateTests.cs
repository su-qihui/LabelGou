using System.IO;
using System.Text;
using LabelGou.App.Export;
using LabelGou.App.ViewModels;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 出纸闸（M8 第 38 棒 · 三道闸之一）：AI 版式落地前先问一句"到底印不印得出东西"。
/// <para>第 36 棒第 6 帧的整张白纸就是从这儿漏的：<c>{{col:…}}</c> 全取不到值 → 四行整条隐藏 →
/// 空版照样进预览（第 33 棒免点头之后这一层没人拦了）。判据放在 <see cref="MainViewModel"/>（可单测），
/// MainWindow 只负责在落地前问它——与「闸门判据在 Core/VM、UI 只做接线」一条边界纪律同构。</para>
/// <para>口径（第 36 棒原话）：<strong>只判全空，不判个别空</strong>——某一列本来就没值是常态。</para>
/// </summary>
public sealed class B38EmptyTemplateGateTests : IDisposable
{
    private readonly string _dir = TestEnvironment.NewTempDir("labelgou-b38");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 临时目录清不掉不影响结论 */ }
    }

    /// <summary>MainViewModel 会碰 WPF 类型，必须在 STA 线程上造。</summary>
    private static T OnSta<T>(Func<T> work)
        => StaWorker.RunAsync<T>((_, _) => work(), null, CancellationToken.None).GetAwaiter().GetResult();

    private MainViewModel NewVm() => new(TestEnvironment.NewTempUiStateStore());

    private string WriteCsv(string name, params string[] headers)
    {
        var path = Path.Combine(_dir, name);
        var text = string.Join(",", headers) + Environment.NewLine +
                   "olu830-35,5" + Environment.NewLine;
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private static LabelTemplate Rows(params string[] contents)
    {
        var spec = new RowLayoutSpec { WidthMm = 140, HeightMm = 100 };
        foreach (var c in contents) spec.Rows.Add(new RowSpec { Content = c, Stretch = false, SizePt = 14 });
        var template = spec.Build();
        Assert.NotNull(template);
        return template!;
    }

    [Fact]
    public void 全空的模板被出纸闸拦下()
    {
        // 第 36 棒第 6 帧的形状：每一行都是引用表里不存在列的 col: 令牌 → 渲染时整条隐藏。
        var template = Rows("{{col:这一列表里没有}}", "{{col:它也没有}}");

        var prints = OnSta(() => NewVm().TemplatePrintsAnything(template));

        Assert.False(prints);
    }

    [Fact]
    public void 个别空放行_只有全空才拦()
    {
        // 一行取得到值、一行取不到：这是常态（某列没值），闸门不许把常态当事故。
        var template = Rows("{{ItemNo}}", "{{col:这一列表里没有}}");

        var prints = OnSta(() => NewVm().TemplatePrintsAnything(template));

        Assert.True(prints);
    }

    [Fact]
    public void 没导数据时退到内置样例_不算空版()
    {
        var template = Rows("{{Origin}}");

        var prints = OnSta(() => NewVm().TemplatePrintsAnything(template));

        Assert.True(prints);
    }

    [Fact]
    public void 有数据时按第一条真记录判()
    {
        var csv = WriteCsv("两列表.csv", "货号 ITEM NO:", "件数 CTN");
        var template = Rows("{{col:件数 CTN}}");

        var prints = OnSta(() =>
        {
            var vm = NewVm();
            vm.LoadSource(csv, null);
            return vm.TemplatePrintsAnything(template);
        });

        // 第 36 棒修 col: 令牌后的口径：AI 模式谁都没绑 → 每列都有 col: 值，这一版该放行。
        Assert.True(prints);
    }
}
