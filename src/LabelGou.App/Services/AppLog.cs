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
    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabelGou", "logs");

    public static string DirectoryPath => Directory_;

    public static void Info(string message) => Write("INFO ", message, null);

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
