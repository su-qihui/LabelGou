using LabelGou.App.Services.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 第 25 棒：「读这张表」卡死 900 秒的两条根因各钉一枚钉子。
/// <para>取证见对接文档 §三-阶段 25：用户机器存的 timeoutSeconds=900 被照单全收
/// （纪律是 180 秒硬顶、超时就报明确失败、禁止自动重试），而共享连接池永不过期
/// ——闲置被网关静默掐断后下一次 POST 挂死在死连接上。</para>
/// </summary>
public class B25AiChannelTests
{
    [Theory]
    [InlineData(900, 180)]     // 用户机器上那份设置的真实形状：存的 900 不许生效
    [InlineData(181, 180)]
    [InlineData(180, 180)]
    [InlineData(120, 120)]     // 顶以下的原样尊重
    [InlineData(5, 10)]        // 下界 10 秒：比这还短就只剩「秒失败」的假象
    [InlineData(0, 10)]
    [InlineData(-7, 10)]
    public void 超时用的时候必须夹在10到180_用户硬顶(int stored, int expected)
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
