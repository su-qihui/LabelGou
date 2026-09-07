using System;
using System.IO;
using System.Runtime.CompilerServices;
using LabelGou.App.Services;

namespace LabelGou.App.Tests;

/// <summary>
/// 测试装配的开场设置与公用夹具。
/// <para>
/// 为什么用 <see cref="ModuleInitializerAttribute"/>：它在程序集里任何测试代码之前跑一次，
/// 不需要每个测试类各自记着调用——记漏一次就会往用户机器上写东西（§五-48 就是这么踩出来的：
/// 单测走导入/识别/界面状态这些会写日志与配置的路径，现场排障时在真实目录里看到一堆根本没发生过的行，
/// 甚至真的生成了 <c>%APPDATA%\LabelGou\uistate.json</c>）。
/// </para>
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void RedirectUserWritablePathsToTemp()
    {
        AppLog.SetDirectoryForTests(NewTempDir("labelgou-test-logs"));
    }

    /// <summary>每次调用给一个新的临时目录（测试类用完自己删）。</summary>
    public static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>给 <see cref="MainViewModel"/> 用的界面状态口，指向临时目录。</summary>
    public static UiStateStore NewTempUiStateStore() => new(NewTempDir("labelgou-uistate"));
}
