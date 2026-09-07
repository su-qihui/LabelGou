using System.Text.Json;
using LabelGou.Core.Data;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 整批固定值（M7）：表里没有那一列、但整批标签共用一个值（厂商表的客户名 BOLAROM 就属于这种）。
/// <para>三条底线都要钉住：表里有值优先、固定值能随方案存回 JSON、空白等于没填。</para>
/// </summary>
public class BatchFixedValueTests
{
    /// <summary>按真表（<c>7.8 金沐 唛头.xlsx</c>）的列形复刻一份，但只有三列——客户名确实不在表里。</summary>
    private static TabularData JinMuLike() => new(
        "样例.xlsx", "Sheet1",
        new[] { "货号\nITEM NO:", "件数\nCTN", "数量\nQTY" },
        new IReadOnlyList<string>[]
        {
            new[] { "olu830-35*144", "5", "144" },
            new[] { "olu830-19*144", "5", "144" },
        },
        0);

    [Fact]
    public void 表里没有的列由整批固定值补上并标清来源()
    {
        var data = JinMuLike();
        var profile = MappingSuggester.Suggest(data.Headers, "金沐 一开四");
        profile.SetFixedValue(MarkFieldKey.Consignee, "BOLAROM");

        var records = RecordMapper.Map(data, profile).Records;

        Assert.Equal(2, records.Count);
        foreach (var record in records)
        {
            var value = record.Get(MarkFieldKey.Consignee);
            Assert.NotNull(value);
            Assert.Equal("BOLAROM", value!.Text);
            // 来源必须能区分开：这个值不在表里，改了方案才会变，界面据此提示用户
            Assert.Equal(ValueOrigin.BatchFixed, value.Origin);
            Assert.Contains("不在表里", value.SourceRef);
        }
    }

    [Fact]
    public void 表里有值时固定值不许覆盖真数据()
    {
        var data = JinMuLike();
        var profile = MappingSuggester.Suggest(data.Headers, "金沐 一开四");
        profile.SetFixedValue(MarkFieldKey.ItemNo, "不该出现的值");

        var records = RecordMapper.Map(data, profile).Records;

        // 固定值不能顶掉表里的真数据（*144 被清洗规则切掉是另一回事，见 ItemNoTailTests）
        Assert.Equal("olu830-35", records[0].GetText(MarkFieldKey.ItemNo));
    }

    [Fact]
    public void 固定值随方案存回JSON不丢()
    {
        var profile = MappingSuggester.Suggest(new[] { "货号", "件数" }, "金沐 一开四");
        profile.SetFixedValue(MarkFieldKey.Consignee, "BOLAROM");

        var back = JsonSerializer.Deserialize<MappingProfile>(JsonSerializer.Serialize(profile))!;

        Assert.Equal("BOLAROM", back.FixedValueFor(MarkFieldKey.Consignee));
        back.SetFixedValue(MarkFieldKey.Consignee, "   ");
        Assert.Null(back.FixedValueFor(MarkFieldKey.Consignee));
    }

    [Fact]
    public void 空白固定值等于没填不进记录()
    {
        var data = JinMuLike();
        var profile = MappingSuggester.Suggest(data.Headers, "金沐 一开四");
        profile.FixedValues[MarkFieldKey.Consignee.ToString()] = "   ";

        var records = RecordMapper.Map(data, profile).Records;

        Assert.False(records[0].Has(MarkFieldKey.Consignee));
    }

    [Fact]
    public void 未知字段名的固定值被安静忽略不炸()
    {
        // 方案 JSON 可能被旧版本或脚本改过：认不出的键不许把整条映射打断
        var data = JinMuLike();
        var profile = MappingSuggester.Suggest(data.Headers, "金沐 一开四");
        profile.FixedValues["NotAField"] = "x";

        var result = RecordMapper.Map(data, profile);

        Assert.Equal(2, result.Records.Count);
        Assert.DoesNotContain(result.Issues, i => i.Message.Contains("NotAField"));
    }
}
