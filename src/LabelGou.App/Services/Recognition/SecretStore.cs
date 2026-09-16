using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LabelGou.App.Services.Recognition;

/// <summary>
/// 只在这台电脑、这个 Windows 登录用户下能解开的秘密存储（DPAPI）。
/// <para>为什么要有它（M7 第 11 棒）：第 9 棒把 API 密钥定成「只活在这次运行」（<c>[JsonIgnore]</c>），
/// 结果用户填完、关窗、再开就没了，界面上只剩一句「云端没有 API 密钥」。用户明确要「保存」，
/// 而<strong>明文存进 <c>%APPDATA%</c> 是第 9 棒已经否掉的路</strong>（这个目录会被备份脚本扫走、店铺电脑会被人接手，§五-11）。
/// 两头都要保住，就只剩加密落盘这一条。</para>
/// <para>为什么用 P/Invoke 而不是 NuGet 包 <c>System.Security.Cryptography.ProtectedData</c>：
/// 那个包的能力与这里完全相同，引它等于为一个 API 破掉本项目从 M1 起的零依赖口径（离线免安装是硬要求，§五-5）。</para>
/// <para><strong>能挡住什么、挡不住什么，界面里必须照说</strong>：密文绑死「这台机 + 这个 Windows 用户」，
/// 拷到另一台机器或另一个账户下解不开（这正是我们要的）；但它挡不住同一个登录用户下的任何程序——
/// 那类威胁模型下环境变量 <c>LABELGOU_LLM_KEY</c> 也不更安全。要更强只能不进云端。</para>
/// </summary>
public static class SecretStore
{
    /// <summary>附加熵：不加它，同一用户下任何程序调 DPAPI 都能解任何密文；加了至少挡掉误读别处的密文。</summary>
    private const string Entropy = "LabelGou.LlmKey.v1";

    private const uint UiForbidden = 0x01;      // CRYPTPROTECT_UI_FORBIDDEN：绝不弹系统对话框
    private const string FileName = "llm-key.protected";

    /// <summary>读文件的结果分三种，「没有」和「解不开」必须分开——后者要红字告诉用户重填。</summary>
    public enum Status
    {
        /// <summary>磁盘上根本没有存过。</summary>
        Missing,
        /// <summary>解出来了。</summary>
        Ok,
        /// <summary>文件在但解不开（换机、换 Windows 用户、文件被截断或篡改）。</summary>
        Unreadable,
    }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabelGou", FileName);

    /// <summary>
    /// 某一家厂商的密文文件：<c>llm-key-&lt;厂商&gt;.protected</c>，放在 <paramref name="legacyPath"/> 的同一目录。
    /// <para>第 80 棒按厂商分密钥（用户：「一个 apikey 是接一个厂商的」）。文件名里带上厂商主机名，
    /// 用户自己进目录也看得出哪份是谁的；槽位里的非法字符换下划线，其余原样保留。</para>
    /// </summary>
    public static string KeyPathFor(string legacyPath, string slot)
    {
        var dir = Path.GetDirectoryName(legacyPath) ?? string.Empty;
        var safe = string.Concat(slot.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(dir, "llm-key-" + safe + ".protected");
    }

    /// <summary>加密成 base64 一行。失败抛 <see cref="InvalidOperationException"/>，带 Win32 错误码。</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) throw new ArgumentException("要加密的内容是空的。", nameof(plain));

        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var inner = Marshal.AllocHGlobal(plainBytes.Length);
        Marshal.Copy(plainBytes, 0, inner, plainBytes.Length);
        var entropy = EntropyBlob.Alloc();
        try
        {
            var dataIn = new DataBlob { cbData = plainBytes.Length, pbData = inner };
            if (!CryptProtectData(ref dataIn, null, entropy.Ptr, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var dataOut))
                throw new InvalidOperationException($"这台电脑没能加密密钥（Windows 错误码 {Marshal.GetLastWin32Error()}）。");

            return Convert.ToBase64String(Take(dataOut));
        }
        finally
        {
            Marshal.FreeHGlobal(inner);
            entropy.Dispose();
        }
    }

    /// <summary>解密。解不开时返回 false 并给一句人话，不抛。</summary>
    public static bool TryUnprotect(string blobBase64, out string? plain, out string? failure)
    {
        plain = null;
        failure = null;
        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(blobBase64.Trim());
        }
        catch (FormatException)
        {
            failure = "存下来的那段密文不是合法的 base64，文件大概被改坏了。";
            return false;
        }

        var inner = Marshal.AllocHGlobal(blob.Length);
        Marshal.Copy(blob, 0, inner, blob.Length);
        var entropy = EntropyBlob.Alloc();
        try
        {
            var dataIn = new DataBlob { cbData = blob.Length, pbData = inner };
            if (!CryptUnprotectData(ref dataIn, out var description, entropy.Ptr, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var dataOut))
            {
                failure = $"这台电脑解不开上次存的密钥（Windows 错误码 {Marshal.GetLastWin32Error()}）。";
                return false;
            }
            try
            {
                plain = Encoding.UTF8.GetString(Take(dataOut));
            }
            finally
            {
                if (description != IntPtr.Zero) LocalFree(description);   // 说明文字也是系统 LocalAlloc 出来的
            }
            return plain is { Length: > 0 };
        }
        catch (DllNotFoundException)
        {
            failure = "这台机器上没有 Windows 的加密组件（crypt32），密钥只能靠环境变量。";
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(inner);
            entropy.Dispose();
        }
    }

    /// <summary>写盘：目录自动建。失败返回 false + 原因（写不进去要让用户知道，别静默）。</summary>
    public static Status TryWriteFile(string path, string plain, out string? failure)
    {
        failure = null;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Protect(plain));
            return Status.Ok;
        }
        catch (Exception ex)
        {
            failure = $"密钥没能存到这台电脑上：{ex.Message}";
            return Status.Unreadable;
        }
    }

    /// <summary>读盘。文件不存在 = <see cref="Status.Missing"/>（正常情况），解不开 = <see cref="Status.Unreadable"/>。</summary>
    public static Status TryReadFile(string path, out string? plain, out string? failure)
    {
        plain = null;
        failure = null;
        if (!File.Exists(path)) return Status.Missing;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            failure = $"存过密钥，但那个文件读不了：{ex.Message}";
            return Status.Unreadable;
        }
        if (string.IsNullOrWhiteSpace(text)) return Status.Missing;

        return TryUnprotect(text, out plain, out failure) ? Status.Ok : Status.Unreadable;
    }

    /// <summary>删掉磁盘上那份。文件不在不算失败。</summary>
    public static string? TryClearFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return null;
        }
        catch (Exception ex)
        {
            return $"磁盘上那份密钥没能删掉：{ex.Message}";
        }
    }

    /// <summary>
    /// 把 API 返回的 DATA_BLOB 里的字节拷成托管数组并归还那块缓冲。
    /// <para><strong>注意释放的是 <c>pbData</c> 本身</strong>：结构体现在住在托管栈上（见下面的 P/Invoke 声明），
    /// 不能再拿结构体地址去 LocalFree。</para>
    /// </summary>
    private static byte[] Take(DataBlob blob)
    {
        try
        {
            var bytes = new byte[blob.cbData];
            if (blob.pbData != IntPtr.Zero && blob.cbData > 0)
                Marshal.Copy(blob.pbData, bytes, 0, blob.cbData);
            return bytes;
        }
        finally
        {
            if (blob.pbData != IntPtr.Zero) LocalFree(blob.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    /// <summary>把附加熵摆成非托管的 DATA_BLOB，用完连内层缓冲一起还。</summary>
    private sealed class EntropyBlob : IDisposable
    {
        private readonly IntPtr _inner;
        private readonly IntPtr _ptr;

        private EntropyBlob(IntPtr inner, IntPtr ptr) { _inner = inner; _ptr = ptr; }

        public IntPtr Ptr => _ptr;

        public static EntropyBlob Alloc()
        {
            var bytes = Encoding.UTF8.GetBytes(Entropy);
            var inner = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, inner, bytes.Length);
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<DataBlob>());
            Marshal.StructureToPtr(new DataBlob { cbData = bytes.Length, pbData = inner }, ptr, false);
            return new EntropyBlob(inner, ptr);
        }

        public void Dispose()
        {
            Marshal.DestroyStructure<DataBlob>(_ptr);
            Marshal.FreeHGlobal(_ptr);
            Marshal.FreeHGlobal(_inner);
        }
    }

    /// <summary>
    /// 两个 API 的最后一个参数是 <c>DATA_BLOB*</c>，<strong>必须声明成 <c>out DataBlob</c></strong>。
    /// <para>写成 <c>out IntPtr</c> 时 CLR 只给 8 字节槽，而系统要往里写 16 字节的结构体——当场写坏栈，
    /// 报出来的是 <c>Internal CLR error 0x80131506</c> / 访问违例，而且栈顶看着像 <c>Marshal.PtrToStructure</c> 的错，
    /// 完全不指向真正的病灶（实测踩过，见 §五-93）。</para>
    /// </summary>
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, out IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
