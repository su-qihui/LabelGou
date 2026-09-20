using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LabelGou.Core.Data;

/// <summary>
/// 导入的表先复制进软件自己的缓存目录再读（第 90 棒②，用户 2026-09-20 给的方案：
/// 「导入表格后暂时把表格存在软件内，切换文件或退出软件清掉文件缓存」）。
/// <para>两件事一起解决。一件是他报的那个：<strong>WPS 说"文件被其他软件使用"</strong>——
/// 读 xlsx 那几处原本用 <c>ZipFile.OpenRead</c>，它在 Windows 上要的是 <c>FileShare.Read</c>，
/// 意思是"我读的时候别人只许读、不许写"；而 WPS / Excel 打开一份表是读+写一起申请的（它要拿编辑锁），
/// 于是我们手上这份读句柄存在的那一段时间里，它拿不到锁 → 屏幕上就是那句"被其他软件使用"。
/// 暂存之后我们只在自己那份副本上反复读，<strong>原件从导入那一刻起就不再碰</strong>。</para>
/// <para>另一件是这么切出来的、他没说的：<strong>导入之后原件还会被反复重读</strong>——① 步改一次切法、
/// 换一个工作表、体检逐条修复、一键修复、撤回，每一趟都整表重读；AI 那边更凶，每轮请求结束都重算一次
/// 表画像，而画像要再开两次 zip 取单元格格式。这些路都拿同一个路径说话，只要他在 WPS 里存过一次盘
/// （哪怕加了一行），内存里那张表和磁盘上那份就对不上了——而 <strong>AI 报的是原表行号</strong>，
/// 行号一错就是剔错行、少印货。读自己那份副本，这个错位从根上没有。</para>
/// <para>副本<strong>按原件路径归一个目录</strong>（里面沿用原文件名）：① 文件名不改，
/// ① 步那句「爆朵唛头 -9.20.xlsx[Sheet1]」与导出元数据都取 <c>Path.GetFileName</c>，下游一个字不用动；
/// ② 两份不同的表各占各的目录，谁也不会把谁正在读的那一份删掉（一个进程里并行读两张表是真实存在的场景，
/// 单测就是这么跑的）；③ 重开同一张表落在同一个目录里，覆盖自己那份，路径不变。</para>
/// </summary>
public static class ImportCache
{
    /// <summary>缓存根目录：<c>%APPDATA%\LabelGou\import-cache</c>。每次问都重算——单测把根目录改到临时盘。</summary>
    public static string Root => Path.Combine(UserPaths.Root, "import-cache");

    /// <summary>暂存的结果。<paramref name="Path"/> 是给下游读的那一份。</summary>
    /// <param name="Path">成功时是缓存里那份副本；退路时是原件本身。</param>
    /// <param name="Copied">是否真的复制成了副本。</param>
    /// <param name="Note">没复制成的原因（缓存目录写不了等）。null = 一切正常。</param>
    public sealed record StageResult(string Path, bool Copied, string? Note);

    /// <summary>
    /// 把一份外部表暂存进缓存，返回该读的那一份。
    /// <para>已经在缓存里的路径原样返回——「换工作表 / 改切法 / 一键修复 / 撤回 / AI 每轮重算画像」
    /// 都拿存下来的路径再读一次，那几趟不该又复制一份。</para>
    /// </summary>
    public static StageResult Stage(string originalPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath)) return new StageResult(originalPath, false, "没给文件路径。");
        var full = Path.GetFullPath(originalPath);
        if (IsCached(full)) return new StageResult(full, true, null);
        if (!File.Exists(full)) return new StageResult(full, false, "文件已经不在了。");

        try
        {
            var folder = FolderFor(full);
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileName(full));

            // FileShare.ReadWrite | Delete = "我只要读，别人要写、要改名、要删都请便"。
            // 对面是 WPS/Excel 时这条尤其重要：它开着这份表的时候我们也还得能读进去。
            using (var src = new FileStream(full, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var dst = File.Create(target))
            {
                src.CopyTo(dst);
            }

            return new StageResult(target, true, null);
        }
        catch (Exception ex)
        {
            // 缓存写不成（只读盘、目录被杀软锁住、漫游配置没建好）不该让他印不出东西：
            // 如实说一句，退回直接读原件。退回那条路照样能干活，只是还会碰原件。
            return new StageResult(full, false, $"{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 丢掉某一份原件的暂存（<strong>换文件</strong>时由界面叫这一句，用户点名的就是这一条）。
    /// <para>只删这一份自己的目录，绝不顺手清整个缓存：那会把另一条正在读表的路的副本一起端掉。</para>
    /// </summary>
    public static void Drop(string originalPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath)) return;
        if (IsCached(originalPath)) return;      // 传进来的是副本路径时，从它反推不出原件，别乱删
        TryDelete(FolderFor(Path.GetFullPath(originalPath)));
    }

    /// <summary>这一份是不是软件自己暂存的副本。</summary>
    public static bool IsCached(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>清掉整个缓存（退出软件走这一句）。</summary>
    public static void Clear()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            foreach (var dir in Directory.EnumerateDirectories(Root)) TryDelete(dir);
            foreach (var file in Directory.EnumerateFiles(Root)) TryDelete(file);
        }
        catch (Exception)
        {
            // 清不掉不是事故：下次退出还会再清，磁盘上多留一份副本而已。
        }
    }

    /// <summary>
    /// 一份原件对应哪个缓存目录：路径（忽略大小写）的 SHA-256 前 16 位。
    /// <para>为什么不用时间戳当目录名：那样"重开同一张表"会每开一次多一份，而且并行读两张表时
    /// "删掉上一份"会误删别人正在用的那一份。按路径归目录，两条都自然成立。</para>
    /// </summary>
    public static string FolderFor(string originalPath)
    {
        var full = Path.GetFullPath(originalPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        return Path.Combine(Root, Convert.ToHexString(hash, 0, 16));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 上面刚读完的那份可能还没被系统放开；留着下次清，不许把导入带崩。
        }
    }
}
