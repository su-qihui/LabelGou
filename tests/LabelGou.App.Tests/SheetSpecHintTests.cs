using System;
using System.Collections.Generic;
using LabelGou.App.Services;
using LabelGou.Core.Data;
using LabelGou.Core.Impos;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 表里那句"这批怎么开纸"的认法（第 101 棒）。用户 2026-09-24 的口径是
/// 「<strong>②调整为看到就修改</strong>」——认到就直接改纸规，不是提示一句等他动手；
/// 线索位置他点了两处（一般在列头，或在模板边写着一组尺寸），例外也给了一条
/// （「开二」一般默认为小一开二）。这几条都在这份判据里，一条一个钉子。
/// </summary>
public class SheetSpecHintTests
{
    [Theory]
    [InlineData("一开四", BuiltInSheetSpecs.IdCut4_280x200)]
    [InlineData("D2 一开四", BuiltInSheetSpecs.IdCut4_280x200)]        // 混在别的话里也认
    [InlineData("一开八", BuiltInSheetSpecs.IdCut8_280x200)]
    [InlineData("大开二 28*20", BuiltInSheetSpecs.IdBig2_280x200)]     // 裸「开二」不许抢在「大开二」前面
    [InlineData("小开二", BuiltInSheetSpecs.IdSmall2_160x240)]
    [InlineData("开二", BuiltInSheetSpecs.IdSmall2_160x240)]           // 他给的例外：光说开二＝小一开二
    [InlineData("一开二", BuiltInSheetSpecs.IdSmall2_160x240)]
    [InlineData("28*20", BuiltInSheetSpecs.IdCut4_280x200)]            // 厂里话是厘米：28*20 = 280×200
    [InlineData("28×20 纸", BuiltInSheetSpecs.IdCut4_280x200)]
    [InlineData("20*28", BuiltInSheetSpecs.IdCut4_280x200)]            // 横竖写反了还是那张纸
    [InlineData("16*24", BuiltInSheetSpecs.IdSmall2_160x240)]
    public void 认到的那句能指到具体哪一档(string text, string expected)
        => Assert.Equal(expected, SheetSpecHints.Detect(text)!.SpecId);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("olu830-35")]
    [InlineData("MADE IN CHINA")]
    public void 表里没写开法就不许动他的纸(string? text)
        => Assert.Null(SheetSpecHints.Detect(text));

    [Fact]
    public void 写着尺寸但没有那一档_只回显不猜一张纸()
    {
        var found = SheetSpecHints.Detect("30*40")!;
        Assert.False(found.MatchesSpec);              // 认到了写法，可对不上任何一档
        Assert.Null(found.SpecId);
        Assert.Equal("30*40", found.Evidence);        // 原话要能回显给他看
    }

    [Fact]
    public void 数据区里那组数字不许当纸规()
    {
        // 体积列写着 12*30——那是货的尺寸，不是纸。行话仍认，尺寸不认（"不猜"那道闸）。
        Assert.Null(SheetSpecHints.Detect("12*30", sizesToo: false));
        Assert.NotNull(SheetSpecHints.Detect("一开四", sizesToo: false));
    }

    [Fact]
    public void 扫一张表_列头与批注都算_数据区只认行话()
    {
        var jargonInData = Table(new[] { "货号 ITEM NO:", "开法" }, Array.Empty<string[]>(),
            new[] { new[] { "olu830-35", "一开八" } });
        Assert.Equal(BuiltInSheetSpecs.IdCut8_280x200, SheetSpecHints.DetectInTable(jargonInData)!.SpecId);

        var sizeInData = Table(new[] { "货号", "体积" }, Array.Empty<string[]>(),
            new[] { new[] { "olu830-35", "28*20" } });
        Assert.Null(SheetSpecHints.DetectInTable(sizeInData));

        var noteAboveHeader = Table(new[] { "货号" }, new[] { new[] { "", "纸：28*20" } },
            new[] { new[] { "olu830-35" } });
        Assert.Equal(BuiltInSheetSpecs.IdCut4_280x200, SheetSpecHints.DetectInTable(noteAboveHeader)!.SpecId);
    }

    /// <summary>
    /// 厂里真给的表形状（仓库里那份按真件逐列复刻的样例 CSV，D 列列头就写着「一开四」）。
    /// <para>这一条是这一棒唯一"拿真数据说话"的证据：其余 <see cref="SheetSpecHintTests"/> 的形状
    /// 都是照用户描述造的。真 <c>.xlsx</c> 在仓库外（14MB 不进仓），2026-09-24 用只读脚本扫过
    /// <c>labelgou-CL</c> 那 6 家，写法分别是「一开四」×3、「张数/一开二」×1、「开二」×1（数据区）、
    /// 以及 TOP 那份<strong>一个字都没写</strong>——全在这张词表的覆盖内。</para>
    /// </summary>
    [Fact]
    public void 真厂商样例的列头那格认得出开法()
    {
        var data = TableImporter.Import(LocateSample("样例-金沐一开四.csv"));

        var found = SheetSpecHints.DetectInTable(data)!;
        Assert.Equal(BuiltInSheetSpecs.IdCut4_280x200, found.SpecId);
        Assert.Equal("一开四", found.Evidence);
    }

    private static string LocateSample(string fileName)
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "samples", fileName);
            if (System.IO.File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException($"找不到样例文件 {fileName}（样例只放仓库里那份复刻 CSV）");
    }

    private static TabularData Table(string[] headers, IReadOnlyList<IReadOnlyList<string>> preamble,
        IReadOnlyList<IReadOnlyList<string>> rows)
        => new("样例.xlsx", "Sheet1", headers, rows, 0, preamble: preamble);

    /// <summary>
    /// 「看到就修改」那一下（用户 2026-09-24 把这条从"提示一句"改成"直接改"）：纸规真的换了，
    /// 而且回显里说清是照哪句话改的——改了纸却不告诉人为什么改，跟切错表一个性质。
    /// </summary>
    [Fact]
    public void 认到就把纸规换掉并说清是哪句话()
    {
        var vm = new ViewModels.MainViewModel(TestEnvironment.NewTempUiStateStore());
        var data = Table(new[] { "货号", "开法" }, Array.Empty<string[]>(), new[] { new[] { "olu830-35", "一开八" } });

        var note = vm.Sheet.ApplySheetFromTable(data);

        Assert.Equal(BuiltInSheetSpecs.IdCut8_280x200, vm.Sheet.SelectedSheetOption!.Spec.Id);
        Assert.Contains("一开八", note);
        Assert.Contains("纸规已照它改成", note);
    }
}
