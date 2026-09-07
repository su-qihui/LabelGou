using System.IO;

namespace LabelGou.App.Services;

/// <summary>
/// 极简本地日志：写到 %APPDATA%\LabelGou\logs\yyyyMMdd.log。
/// <para>
/// 面向的场景很具体——打印店机器出问题时报错窗口一闪而过、操作员说不清，
/// 我们只能看日志。所以宁可啰嗦，也不许静默吞异常。
/// </para>
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private static string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabelGou", "logs");

    public static string DirectoryPath => Directory_;

    /// <summary>
    /// 把日志目录改到别处（<strong>只给测试用</strong>）。
    /// <para>
    /// 为什么开这个口：单测会走导入/识别/界面状态这些会写日志的路径，于是现场排障时
    /// 会在用户真实日志里看到一堆“底稿导入成功：底稿.svg”这种根本没发生过的行（§五-48）。
    /// 测试装配一开头就把目录指到临时文件夹，产品行为不变。
    /// </para>
    /// </summary>
    public static void SetDirectoryForTests(string directory) => Directory_ = directory;

    public static void Info(string message) => Write("INFO ", message, null);

    /// <summary>不算故障但应当留痕的情况（比如用户操作被校验拦下）。</summary>
    public static void Warning(string message) => Write("WARN ", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + "        " + exception.GetType().FullName + ": " + exception.Message;
            var inner = exception.InnerException;
            var depth = 0;
            while (inner is not null && depth++ < 5)
            {
                line += Environment.NewLine + "        <- " + inner.GetType().FullName + ": " + inner.Message;
                inner = inner.InnerException;
            }
            line += Environment.NewLine + "        " + (exception.StackTrace ?? "(无堆栈)").Replace("\n", "\n        ");
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Directory_);
                File.AppendAllText(
                    Path.Combine(Directory_, DateTime.Now.ToString("yyyyMMdd") + ".log"),
                    line + Environment.NewLine,
                    System.Text.Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // 日志本身失败不能再抛异常把程序带崩，退到调试输出
            System.Diagnostics.Debug.WriteLine(line);
        }
    }

    /// <summary>最近若干行日志（关于对话框/诊断用）。</summary>
    public static string Tail(int lines = 30)
    {
        try
        {
            var file = Path.Combine(Directory_, DateTime.Now.ToString("yyyyMMdd") + ".log");
            if (!File.Exists(file)) return "(今天还没有日志)";
            var all = File.ReadAllLines(file);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (IOException)
        {
            return "(日志暂时被占用，读不出来)";
        }
    }
}
