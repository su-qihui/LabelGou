using LabelGou.App.Services.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 25 棒：「读这张表」卡死 900 秒的两条根因各钉一枚钉子（超时口径在**同日第 27 棒**被用户改过：
/// 180 硬顶作废、0＝不设限，本文件的断言已按新口径同步；连接池 30 秒换新不变）。
/// <para>取证见对接文档 §三-阶段 25/27：死连接复用（网关掐闲置线、下一次 POST 挂死）是真根因，
/// 超时只是把人吊在死线上的那根绳。</para>
/// </summary>
public class B25AiChannelTests
{
    [Theory]
    [InlineData(900, 900)]     // 第 27 棒起用户的值被尊重（180 夹顶已按用户指令作废）
    [InlineData(181, 181)]
    [InlineData(180, 180)]
    [InlineData(120, 120)]
    [InlineData(5, 10)]        // 下界 10 秒保留：比这还短就只剩「秒失败」的假象
    [InlineData(0, 0)]         // 0 = 不设限，只靠手动停止（第 27 棒新口径）
    [InlineData(-7, 0)]        // 负数按不限收，不替用户编一个正数
    public void 超时生效值_0是不限_其余下限10秒_不再有180硬顶(int stored, int expected)
        => Assert.Equal(expected, new RecognitionSettings { TimeoutSeconds = stored }.EffectiveTimeoutSeconds);

    [Fact]
    public void 默认值是180_恰在硬顶上()
        => Assert.Equal(180, new RecognitionSettings().EffectiveTimeoutSeconds);

    [Fact]
    public void 共享handler必须给连接寿命上限_闲置久了整条换新()
    {
        using var handler = DualStackConnect.NewHandler();

        // 默认是无限（Infinite）——那就是「第一次好、隔几分钟第二次挂」的那条死连接通道。
        Assert.Equal(TimeSpan.FromSeconds(30), handler.PooledConnectionLifetime);
    }
}
