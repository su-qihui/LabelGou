// Win7 变体：Core 目标降到 net6.0，而下面几处用的是 .NET 7 才加进 BCL 的便捷 API
// （char.IsAsciiDigit / ArgumentException.ThrowIfNullOrEmpty / Stream.ReadAtLeast）。
// 它们与 Win7 无关，纯粹是框架版本差——在此按 .NET 7 的语义等价实现，调用点改走这里。
// 刻意放全局命名空间：省去给每个调用文件加 using；internal 不对外暴露。
// 若哪天把 TFM 升回 net7+，可把调用点换回 BCL 原样并删掉本文件。
using System.Runtime.CompilerServices;

internal static class Net6Compat
{
    /// <summary>等价于 .NET 7 的 <c>char.IsAsciiDigit</c>：仅 ASCII '0'..'9'。</summary>
    public static bool IsAsciiDigit(char c) => (uint)(c - '0') <= (uint)('9' - '0');

    /// <summary>
    /// 等价于 .NET 7 的 <c>ArgumentException.ThrowIfNullOrEmpty</c>：
    /// null 抛 <see cref="ArgumentNullException"/>、空串抛 <see cref="ArgumentException"/>，paramName 由调用处表达式自动取。
    /// </summary>
    public static void ThrowIfNullOrEmpty(string? argument, [CallerArgumentExpression("argument")] string? paramName = null)
    {
        if (argument is null)
            throw new ArgumentNullException(paramName);
        if (argument.Length == 0)
            throw new ArgumentException("值不能为空字符串。", paramName);
    }

    /// <summary>
    /// 等价于 .NET 7 的 <c>Stream.ReadAtLeast</c>：反复读直到凑满 <paramref name="minimumBytes"/> 或到流尾；
    /// 没凑满且 <paramref name="throwOnEndOfStream"/> 为真才抛 <see cref="EndOfStreamException"/>，否则返回实际读到的字节数。
    /// </summary>
    public static int ReadAtLeast(Stream stream, Span<byte> buffer, int minimumBytes, bool throwOnEndOfStream = true)
    {
        if ((uint)minimumBytes > (uint)buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(minimumBytes), minimumBytes, "minimumBytes 必须介于 0 与 buffer.Length 之间。");
        if (minimumBytes == 0)
            return 0;

        int totalRead = 0;
        while (totalRead < minimumBytes)
        {
            int bytesRead = stream.Read(buffer.Slice(totalRead));
            if (bytesRead == 0)
                break;
            totalRead += bytesRead;
        }

        if (totalRead < minimumBytes && throwOnEndOfStream)
            throw new EndOfStreamException();
        return totalRead;
    }
}
