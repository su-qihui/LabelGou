using System.Security.Cryptography;
using System.Text;

namespace LabelGou.Core.Agent;

/// <summary>
/// 外部 agent 连进来时的那道门：只认回环地址 + 只认这一句令牌。
/// <para>为什么要有：这个服务听在 <c>127.0.0.1</c> 上，但它开着的这段时间里，
/// 这台机器上任何进程都能连（包括浏览器里一段脚本——虽然它连不上回环以外的口）。
/// 令牌的作用不是防"外人"，是防"不是我们约好的那个程序"。</para>
/// <para>口令比对故意写成逐字节异或累加：<c>string.StartsWith</c> 那种短路比较耗时随匹配位数变化，
/// 是一个能测着用的侧信道。这里不图快，图"猜不出来"。</para>
/// </summary>
public static class AgentAuth
{
    /// <summary>本次会话用的一次性令牌（32 个十六进制位）。只活内存，<strong>不写盘</strong>。</summary>
    public static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// 请求头 <c>Authorization</c> 认不认。允许 <c>Bearer xxx</c> 与裸令牌两种写法
    /// （外部程序哪家都有，宽容的是格式，不是权限）。
    /// </summary>
    public static bool TokenMatches(string? authorizationHeader, string expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken)) return false;   // 没设令牌 = 谁都不许进
        var given = StripBearer(authorizationHeader);
        return given is not null && FixedTimeEquals(given, expectedToken);
    }

    /// <summary>只有本机算"自己人"。<c>localhost</c> 这种写法也认，因为解析完就是回环。</summary>
    public static bool IsLoopbackHost(string? host) => host switch
    {
        null or "" => false,
        "localhost" => true,
        "127.0.0.1" => true,
        "::1" => true,
        _ => host.StartsWith("127.", StringComparison.Ordinal),
    };

    /// <summary>来源地址能不能收。<paramref name="remoteIp"/> 是从 socket 上读到的字符串。</summary>
    public static bool AllowsRemote(string? remoteIp) => IsLoopbackHost(remoteIp);

    private static string? StripBearer(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var text = header.Trim();
        const string scheme = "Bearer ";
        return text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? text[scheme.Length..].Trim()
            : text;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);
        if (left.Length == 0 || right.Length == 0) return false;

        // 长度不等就 false：这里"泄"的只有位数，而我们自己的令牌长度固定（32）。
        var diff = left.Length ^ right.Length;
        var limit = Math.Min(left.Length, right.Length);
        for (var i = 0; i < limit; i++) diff |= left[i] ^ right[i];
        return diff == 0;
    }
}
