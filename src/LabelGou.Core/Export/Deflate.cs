using System.IO.Compression;

namespace LabelGou.Core.Export;

/// <summary>
/// 一份裸 zlib 流（PDF 的 FlateDecode 与 TIFF 的 Compression=8 用的是同一种外壳）。
/// <para>为什么自己包：<see cref="DeflateStream"/> 只给裸 deflate，而这两处都要 zlib 的
/// 2 字节头 + 4 字节 Adler-32 尾。第 3 棒的 PDF 里那份私有实现搬到这儿，PDF 与 TIFF 共用一条，
/// 免得两处各自错一遍。</para>
/// </summary>
public static class Deflate
{
    public static byte[] Compress(byte[] raw)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(0x78);       // CMF：deflate + 32K 窗口
        buffer.WriteByte(0x01);       // FLG：无预设字典、最快档（校验位补齐，(0x78<<8|0x01) % 31 == 0）
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }
        var checksum = new byte[4];
        WriteBigEndian(checksum, Adler32(raw));
        buffer.Write(checksum, 0, 4);
        return buffer.ToArray();
    }

    /// <summary>解压一段 zlib 流。写出口的人必须能用同一份工具读回自己写的东西，不然"写出去了"只是自说自话。</summary>
    public static byte[] Decompress(byte[] zlib)
    {
        if (zlib.Length < 6) throw new InvalidDataException("zlib 流太短。");
        using var input = new MemoryStream(zlib, 2, zlib.Length - 6);     // 跳过头 2 字节与尾 4 字节
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static void WriteBigEndian(byte[] target, uint value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static uint Adler32(byte[] data)
    {
        const uint modulus = 65521;
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }
        return (b << 16) | a;
    }
}
