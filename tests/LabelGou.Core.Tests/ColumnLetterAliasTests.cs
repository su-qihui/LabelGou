using LabelGou.Core.Data;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 按 Excel 列号直取那一列（第 99 棒，用户：「不用去绑定列表后再置入，可以直接调用 ABC 列填入代替」，
/// 又补一句「这只是手动调整更方便的方式……不一定只有 ABC，按表格里有的来」）。
/// <para>三条钉住：<strong>①</strong> 列号确实取到那一列（不止 A/B/C）；<strong>② 表头占先</strong>——
/// 有一列表头就叫 "B" 时那个键归它，列号别名不许覆盖（一个键在一张表里不许有两个意思）；
/// <strong>③</strong> 绑过的列别名带的是<strong>同一个处理过的值</strong>，货号切 <c>*</c> 尾巴那套
/// 不许因为换了取法就被绕过去（第 36 棒那张白纸的原话）。</para>
/// </summary>
public class ColumnLetterAliasTests
{
    private static TabularData Sheet(string[] headers, string[] values) => new(
        "样例.xlsx", "Sheet1", headers,
        new IReadOnlyList<string>[] { values },
        0);

    private static readonly string[] JinMuHeaders =
        { "货号\nITEM NO:", "件数\nCTN", "数量\nQTY" };

    private static readonly string[] JinMuRow = { "olu830-35*144", "5", "144" };

    [Fact]
    public void 没绑的列也能按列号取到值()
    {
        var data = Sheet(JinMuHeaders, JinMuRow);
        var record = RecordMapper.Map(data, MappingProfile.CreateFor(data.Headers)).Records.Single();

        Assert.Equal("olu830-35*144", record.GetCustom("col:A")!.Text);
        Assert.Equal("5", record.GetCustom("col:B")!.Text);
        Assert.Equal("144", record.GetCustom("col:C")!.Text);
        // 表头那份一个字不动：换个取法不该把老写法弄坏
        Assert.Equal("144", record.GetCustom("col:数量\nQTY")!.Text);
    }

    [Fact]
    public void 列号不止前三个按表格实际列数走()
    {
        var headers = Enumerable.Range(0, 30).Select(i => $"列{i}").ToArray();
        var values = Enumerable.Range(0, 30).Select(i => $"v{i}").ToArray();
        var data = Sheet(headers, values);
        var record = RecordMapper.Map(data, MappingProfile.CreateFor(data.Headers)).Records.Single();

        Assert.Equal("v25", record.GetCustom("col:Z")!.Text);      // 第 26 列才是 Z
        Assert.Equal("v26", record.GetCustom("col:AA")!.Text);     // 越过单字母那一圈
        Assert.Equal("v29", record.GetCustom("col:AD")!.Text);
    }

    [Fact]
    public void 表头占先时列号别名不许顶掉那一列()
    {
        // "B" 这个表头放在**第一列**（列号也是 A 那一列）：这样别名要是没让路，第二列（列号 B）的 "5"
        // 会在循环里晚一步把表头那份顶掉——顺序正好是漏掉守卫时唯一会坏的那一种。
        var data = Sheet(new[] { "B", "件数\nCTN", "重量" }, new[] { "表头那列的值", "5", "22" });
        var record = RecordMapper.Map(data, MappingProfile.CreateFor(data.Headers)).Records.Single();

        Assert.Equal("表头那列的值", record.GetCustom("col:B")!.Text);
        Assert.Equal("22", record.GetCustom("col:重量")!.Text);     // 第二列仍只能用表头取：它的列号被占了就不该再塞别名
        Assert.Equal("5", record.GetCustom("col:件数\nCTN")!.Text);
    }

    [Fact]
    public void 绑过的列走的是同一个处理过的值()
    {
        var data = Sheet(JinMuHeaders, JinMuRow);
        var profile = MappingProfile.CreateFor(data.Headers);
        profile.Bind(MarkFieldKey.ItemNo, 0);
        var record = RecordMapper.Map(data, profile).Records.Single();

        // 货号绑上了字段 → 值被切掉 *144 尾巴；列号别名必须拿到**切完的那份**，
        // 否则同一条数据换个取法就印出两个样（第 36 棒那台白纸的另一面）。
        Assert.Equal("olu830-35", record.Get(MarkFieldKey.ItemNo)!.Text);
        Assert.Equal("olu830-35", record.GetCustom("col:A")!.Text);
        Assert.Equal("olu830-35", record.GetCustom("col:货号\nITEM NO:")!.Text);
    }
}
