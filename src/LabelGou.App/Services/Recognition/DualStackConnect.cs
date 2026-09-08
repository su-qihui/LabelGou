using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;

namespace LabelGou.App.Services.Recognition;

/// <summary>
/// 双栈交错连接 —— 治「这台机器有 IPv6 地址，但没有 IPv6 出口」时请求一路挂到超时的坑。
/// <para>为什么要有这个文件（第 10 棒本机实测，不是推测）：百炼 <c>dashscope.aliyuncs.com</c>
/// 的 DNS 把 <b>IPv6 排在前面</b>，而 <c>Test-NetConnection [2408:…]:443</c> 与 <c>curl -6</c>
/// 实测都连不上（<c>curl -6</c> 挂满 12 秒 exit=28），<c>curl -4</c> 却 0.27 秒拿到 401。
/// .NET 的 <see cref="SocketsHttpHandler"/> 默认按解析顺序<b>逐个</b>试、不交错回退，
/// 于是先撞黑洞地址就一路挂到 <c>CancelAfter</c>，界面上只剩一句「拉模型列表超时」，
/// 用户以为是云端坏了。<b>curl 有 Happy Eyeballs 所以逃过，我们必须自己做一遍。</b></para>
/// <para>做法：接管连接这一步，IPv4 先发起、之后每个候选地址隔
/// <see cref="AddressHeadStart"/> 再发起，谁先连上用谁；全失败才报错，
/// 且报的是分诊过的人话（解析失败 / 拒绝连接 / 无出口 / TLS 各一句），不再统一糊成「超时」。</para>
/// </summary>
public static class DualStackConnect
{
    /// <summary>第一个地址发起后，隔多久再发起下一个（照 curl happy eyeballs 的交错口径）。</summary>
    public static readonly TimeSpan AddressHeadStart = TimeSpan.FromMilliseconds(200);

    /// <summary>单个地址的连接预算：到点就放弃它去看别的，不让一个黑洞吃满整次请求。</summary>
    public static readonly TimeSpan PerAddressBudget = TimeSpan.FromSeconds(8);

    /// <summary>带双栈回退连接的处理器。只给真发 HTTP 的共享客户端用；单测注入假 handler 时不走这里。
    /// <para>签名事实（编译器实测，不是凭印象）：.NET 8 的 <c>ConnectCallback</c> 是
    /// <c>Func&lt;SocketsHttpConnectionContext, CancellationToken, ValueTask&lt;Stream&gt;&gt;</c>；
    /// 带 <c>ConnectCallbackContext</c> 的重载是 .NET 9 才有的，本机 SDK = 8.0.424 用不了。</para></summary>
    public static SocketsHttpHandler NewHandler() => new() { ConnectCallback = OpenStreamAsync };

    /// <summary>
    /// 连接顺序：IPv4 全部排在 IPv6 前面，同族内保持 DNS 给的顺序（<c>OrderBy</c> 是稳定排序）。
    /// <para>为什么 IPv4 先：本机实测 IPv6 是无出口的黑洞，让它排前面等于每次白等 8 秒；
    /// 而真只有 IPv6 可达的网络也不会因此坏——隔 200ms 照样发起 IPv6。</para>
    /// </summary>
    public static IReadOnlyList<IPAddress> OrderForConnect(IEnumerable<IPAddress> addresses) =>
        addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0).ToList();   // 稳定排序：族内保持 DNS 原序

    private static async ValueTask<Stream> OpenStreamAsync(
        SocketsHttpConnectionContext context, CancellationToken cancel)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = OrderForConnect(await Dns.GetHostAddressesAsync(host, cancel).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw new HttpRequestException($"域名解析不出来：{host}（{Classify(ex)}）", ex);
        }
        if (addresses.Count == 0)
            throw new HttpRequestException($"域名解析结果为空：{host}（这台机器的 DNS 现在不可用）");

        Socket socket;
        try
        {
            socket = await ConnectAnyAsync(addresses, port, cancel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var tried = string.Join("、", addresses.Select(DescribeAddress));
            throw new HttpRequestException($"连不上 {host}:{port}（试过 {tried}）：{Classify(ex)}", ex);
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>交错并发地连所有候选地址，先连上者胜；全失败抛出最后一个异常。</summary>
    private static async Task<Socket> ConnectAnyAsync(
        IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancel)
    {
        // race 不能随方法返回就 dispose：落选的尝试还在后台跑，它们引用了这个 token。
        var race = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var tasks = new List<Task<Socket>>(addresses.Count);
        for (var i = 0; i < addresses.Count; i++)
        {
            var address = addresses[i];
            var headStart = TimeSpan.FromMilliseconds(i * AddressHeadStart.TotalMilliseconds);
            tasks.Add(Task.Run(async () =>
            {
                if (headStart > TimeSpan.Zero) await Task.Delay(headStart, race.Token).ConfigureAwait(false);
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(race.Token);
                budget.CancelAfter(PerAddressBudget);
                try
                {
                    await socket.ConnectAsync(address, port, budget.Token).ConfigureAwait(false);
                    return socket;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }, race.Token));
        }

        var pending = new List<Task<Socket>>(tasks);
        Socket? winner = null;
        Exception? last = null;
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            try
            {
                var socket = await done.ConfigureAwait(false);
                if (winner is null) { winner = socket; break; }
                socket.Dispose();            // 后面才连上的多余套接字，立刻关掉
            }
            catch (Exception ex) { last = ex; }
        }

        if (winner is null)
        {
            race.Dispose();
            throw last ?? new SocketException((int)SocketError.NetworkUnreachable);
        }

        // 拿到胜者后绝不等待落选任务：本机实测落选的 IPv6 要挂 8 秒才失败，
        // 等它们就把 0.05 秒能连上的请求拖成 9 秒。只叫停它们，并在后台收尸：
        // 成功的多余套接字关掉，失败的异常必须被观察，否则会在终结器线程上冒 UnobservedTaskException。
        race.Cancel();
        foreach (var rest in pending)
        {
            _ = rest.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) t.Result.Dispose();
                else _ = t.Exception;
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
        if (pending.Count > 0)
        {
            _ = Task.WhenAll(pending).ContinueWith(
                _ => race.Dispose(), TaskContinuationOptions.ExecuteSynchronously);
        }
        else
        {
            race.Dispose();
        }
        return winner;
    }

    /// <summary>
    /// 调用点用的总入口：把异常链上每一层的人话拼起来（已被外层包含的不重复说）。
    /// <para>为什么不能只取最内层：连接层抛的那句带「连不上谁、试过哪些地址」，
    /// 只报内层的「TCP 连接超时」就又退回看不见的「超时」了。</para>
    /// </summary>
    public static string Talk(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = DescribeOne(e);
            if (string.IsNullOrWhiteSpace(msg)) continue;
            if (parts.Any(p => p.Contains(msg, StringComparison.Ordinal))) continue;
            parts.RemoveAll(p => msg.Contains(p, StringComparison.Ordinal));
            parts.Add(msg.Trim());
            if (parts.Count >= 3) break;         // 三层就够，再往下拼是自我介绍
        }
        return parts.Count > 0 ? string.Join("；", parts) : "未知网络错误";
    }

    /// <summary>把网络异常分诊成人话。调用方负责在前面拼「连不上谁、试过哪些地址」。</summary>
    public static string Classify(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SocketException se:
                    return SocketTalk(se);
                case AuthenticationException ae:
                    return $"TLS 握手失败：{ae.Message}";
                case OperationCanceledException:
                    continue;           // 往内层看，通常包着一个真异常；都没有时下面按超时收
            }
        }
        return ex is OperationCanceledException
            ? "连接被取消或超时"
            : ex.Message;
    }

    private static string DescribeOne(Exception e) => e switch
    {
        SocketException se => SocketTalk(se),
        AuthenticationException ae => $"TLS 握手失败：{ae.Message}",
        _ => e.Message,
    };

    private static string SocketTalk(SocketException se) => se.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
            => $"域名解析失败：{se.Message}",
        SocketError.ConnectionRefused
            => "对方拒绝连接（服务没起或端口不对；本机 Ollama 没跑就是这个）",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown
            => "这台机器到它没有网络出口（防火墙、断网，或只有地址没有路由的 IPv6）",
        SocketError.TimedOut or SocketError.OperationAborted
            => "TCP 连接超时（地址连不上：多半是被防火墙吃了，或这条协议栈没有出口）",
        _ => $"网络错误：{se.Message}",
    };

    private static string DescribeAddress(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 ? $"IPv6 {a}" : $"IPv4 {a}";
}
