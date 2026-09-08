using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using LabelGou.App.Services.Recognition;
using Xunit;

namespace LabelGou.App.Tests;

/// <summary>
/// 双栈交错连接（<see cref="DualStackConnect"/>）的单测 —— 第 10 棒，治「模型列表拉不到」。
/// <para>用户看到的是一句「拉模型列表超时」，本机实测真相是：百炼的 DNS 把 IPv6 排在前面，
/// 而这台机器<b>没有 IPv6 出口</b>（<c>curl -6</c> 挂满 12 秒、<c>curl -4</c> 0.27 秒拿 401），
/// .NET 默认按解析顺序<b>逐个</b>试，于是先撞黑洞就一路挂到超时。</para>
/// <para>这里钉住两件可确定的事：候选地址的<b>顺序</b>与错误<b>分诊文案</b>。
/// 真网络那条 A/B 证据在 <c>_probe\b10-net\</c>（默认 handler 25.02 秒失败 vs 双栈 0.27 秒拿 401），
/// 下面另有一条 env-gated 的真连测试，口径同 §十-A-16：默认不跑，不假装跑过。</para>
/// </summary>
public class DualStackConnectTests
{
    private static IPAddress V4(string s) => IPAddress.Parse(s);

    private static IPAddress V6(string s) => IPAddress.Parse(s);

    [Fact]
    public void IPv4排在IPv6前面且同族保持DNS原序()
    {
        var dnsOrder = new[]
        {
            V6("2408:400a:3e:ef00:ce6f:bc78:1534:33d0"),   // 本机实测：DNS 先给 IPv6
            V4("39.96.213.166"),
            V6("2408:400a:3e:ef02:12f:bd95:e827:51d"),
            V4("8.140.217.18"),
        };

        var ordered = DualStackConnect.OrderForConnect(dnsOrder);

        // 前两个必须是 IPv4，而且族内顺序 = DNS 给的顺序（稳定排序，不许被搅乱）；
        // 后两个才是原来排在最前的那两条 IPv6。
        Assert.Equal(
            new[] { "39.96.213.166", "8.140.217.18", "2408:400a:3e:ef00:ce6f:bc78:1534:33d0", "2408:400a:3e:ef02:12f:bd95:e827:51d" },
            ordered.Select(a => a.ToString()));
        Assert.All(ordered.Take(2), a => Assert.Equal(AddressFamily.InterNetwork, a.AddressFamily));
        Assert.All(ordered.Skip(2), a => Assert.Equal(AddressFamily.InterNetworkV6, a.AddressFamily));
    }

    [Fact]
    public void 全是IPv6时不重排()
    {
        var onlyV6 = new[] { V6("2408::1"), V6("2408::2") };

        Assert.Equal(onlyV6.Select(a => a.ToString()), DualStackConnect.OrderForConnect(onlyV6).Select(a => a.ToString()));
    }

    [Fact]
    public void 连接超时被分诊成看得懂的一句()
    {
        var ex = new SocketException((int)SocketError.TimedOut);

        var talk = DualStackConnect.Talk(ex);

        Assert.Contains("TCP 连接超时", talk, StringComparison.Ordinal);
        Assert.DoesNotContain("A task was canceled", talk, StringComparison.Ordinal);
    }

    [Fact]
    public void 拒绝连接说的是服务没起而不是超时()
    {
        var talk = DualStackConnect.Talk(new SocketException((int)SocketError.ConnectionRefused));

        Assert.Contains("拒绝连接", talk, StringComparison.Ordinal);
    }

    [Fact]
    public void 外层人话与内层分诊两句都在不互相顶掉()
    {
        // 真实形状：外层带「试过哪些地址」，内层才是 SocketException。
        // 这条要同时钉住两点：丢了外层就看不出在连谁，丢了内层就只剩一句「One or more errors」。
        var inner = new SocketException((int)SocketError.HostUnreachable);
        var outer = new HttpRequestException(
            "连不上 dashscope.aliyuncs.com:443（试过 IPv4 8.140.217.18、IPv6 2408:400a::1）", inner);

        var talk = DualStackConnect.Talk(outer);

        Assert.Contains("试过 IPv4 8.140.217.18", talk, StringComparison.Ordinal);        // 外层原话保留
        Assert.Contains("这台机器到它没有网络出口", talk, StringComparison.Ordinal);   // 内层被分诊成中文，不是裸异常文本
    }

    [Fact]
    public void 处理器带上了自己的连接回调()
    {
        using var handler = DualStackConnect.NewHandler();

        Assert.NotNull(handler.ConnectCallback);
    }

    /// <summary>
    /// 真网络 A/B 的常驻版：只在 <c>LABELGOU_TEST_NET=1</c> 时跑（§十-A-16 的既有口径 ——
    /// xunit 2.9.2 没有 <c>Assert.Skip</c>，宁可默认不跑，也不把它做成一条永远绿的假测试）。
    /// <para>断言的是「0.05 秒能连上的 IPv4 不该被落选的 IPv6 拖慢」：整次请求 5 秒内拿到 HTTP 状态码。</para>
    /// </summary>
    [Fact]
    public async Task 双栈handler真连百炼应当在5秒内拿到响应()
    {
        if (Environment.GetEnvironmentVariable("LABELGOU_TEST_NET") != "1") return;

        using var handler = DualStackConnect.NewHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://dashscope.aliyuncs.com/compatible-mode/v1/models");
        request.Headers.Add("Authorization", "Bearer probe-no-real-key");

        var response = await client.SendAsync(request, cts.Token);       // 不带真密钥：期望 401，而不是挂住

        Assert.True((int)response.StatusCode is > 0, $"没拿到任何 HTTP 状态码：{response.StatusCode}");
    }
}
