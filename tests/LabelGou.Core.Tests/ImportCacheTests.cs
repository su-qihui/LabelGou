using System;
using System.IO;
using System.Linq;
using LabelGou.Core;
using LabelGou.Core.Data;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 第 90 棒②：导入的表先暂存进软件自己的缓存，之后不再碰原件。
/// <para>用户口径「导入表格后暂时把表格存在软件内，切换文件或退出软件清掉文件缓存」，起因是
/// 「导入 xlsx 后后台再调用 WPS 存在文件无法使用、被其他软件使用」。</para>
/// <para>这一类把 <see cref="UserPaths"/> 整个根目录改到临时盘（Core 侧此前没有任何测试读它，
/// 各库都是显式传目录进去的，所以这一句只影响本类）。</para>
/// </summary>
public sealed class ImportCacheTests : IDisposable
{
    static ImportCacheTests()
        => UserPaths.SetRootForTests(NewDir("labelgou-b90-userroot"));

    private readonly string _dir = NewDir("labelgou-b90");

    public ImportCacheTests() => ImportCache.Clear();   // 每条判据都从空缓存起步（同一个临时根目录被这一类的用例共用）

    private static string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string WriteTable(string name, string body = "客户,件数\n爆朵,144\n")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, body, new System.Text.UTF8Encoding(true));
        return path;
    }

    [Fact]
    public void 暂存进缓存_文件名原样保留_原件一个字节不动()
    {
        var original = WriteTable("爆朵唛头 -9.20.xlsx");
        var before = File.ReadAllBytes(original);

        var staged = ImportCache.Stage(original);

        Assert.True(staged.Copied, staged.Note ?? "复制成了");
        Assert.NotEqual(Path.GetFullPath(original), staged.Path);
        Assert.StartsWith(Path.GetFullPath(ImportCache.Root), staged.Path);
        // 沿用原文件名：① 步那句「爆朵唛头 -9.20.xlsx[Sheet1]」和导出元数据都取 GetFileName，不改名下游一个字不用动
        Assert.Equal("爆朵唛头 -9.20.xlsx", Path.GetFileName(staged.Path));
        Assert.Equal(before, File.ReadAllBytes(staged.Path));
        Assert.Equal(before, File.ReadAllBytes(original));   // 原件没被我们动过一根手指
    }

    [Fact]
    public void 换一张表_上一张的暂存跟着丢掉()
    {
        // 用户点名的口径：「切换文件或退出软件清掉文件缓存」。丢的只许是上一张自己那个目录——
        // 清整个缓存会把另一条正在读表的路的副本一起端掉（并行跑的单测就是这么死的）。
        var firstOriginal = WriteTable("上一张表.csv");
        var first = ImportCache.Stage(firstOriginal).Path;
        var secondOriginal = WriteTable("这一张表.csv");
        var second = ImportCache.Stage(secondOriginal).Path;

        Assert.True(File.Exists(first));          // 还没"切换"，两张各自在，谁也不许碰谁
        Assert.True(File.Exists(second));
        Assert.NotEqual(ImportCache.FolderFor(firstOriginal), ImportCache.FolderFor(secondOriginal));

        ImportCache.Drop(firstOriginal);

        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));         // 在用的那一份一根手指都没动
    }

    [Fact]
    public void 重开同一张表_落回同一个目录_路径不变()
    {
        // 路径不变这一条是给"重开同一张表不算换表"用的：换了目录就等于换了文件，
        // 他手工调过的切法会被当成新表清掉。
        var original = WriteTable("同一张.csv");
        var first = ImportCache.Stage(original).Path;
        var again = ImportCache.Stage(original);

        Assert.True(again.Copied);
        Assert.Equal(first, again.Path);
        Assert.Single(Directory.EnumerateDirectories(ImportCache.Root));
    }

    [Fact]
    public void 重读缓存里那一份_不再复制第二份()
    {
        // 换工作表 / 改切法 / 一键修复 / 撤回 / AI 每轮重算画像都拿存下来的路径再读一次，
        // 那几趟要是每次都复制一份，缓存里就堆十几份同一张表。
        var first = ImportCache.Stage(WriteTable("同一张二.csv")).Path;
        var again = ImportCache.Stage(first);

        Assert.True(again.Copied);
        Assert.Equal(first, again.Path);
        Assert.Single(Directory.EnumerateDirectories(ImportCache.Root));
    }

    [Fact]
    public void WPS正开着这份文件也暂存得动()
    {
        // 对面（WPS/Excel）开一份表通常允许别人读、不允许别人写。我们只要读，且给出去 ReadWrite|Delete，
        // 所以两边都不该卡。这一条测的是"我们读得动他锁着的文件"。
        var original = WriteTable("被WPS开着.csv");
        using (var holding = new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            // 反证：老写法（ZipFile.OpenRead / File.ReadAllBytes）内部要的就是 FileShare.Read 这一档，
            // 在这种局面下根本开不开——他那句"文件被其他软件使用"的机械原因就在这儿。
            Assert.Throws<IOException>(() => File.ReadAllBytes(original));

            var staged = ImportCache.Stage(original);
            Assert.True(staged.Copied, staged.Note ?? "复制成了");
            using var read = new FileStream(staged.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.Contains("爆朵", new StreamReader(read).ReadToEnd());
        }
    }

    [Fact]
    public void 暂存之后删掉原件_这张表照样读得完整()
    {
        // 这一条是"不再碰原件"的正证：原件没了还能整表读出来，说明下游没有一条路回头去开它。
        var original = WriteTable("删得掉的.csv", "客户,件数\n爆朵,144\n齐辉,88\n");
        var staged = ImportCache.Stage(original).Path;
        File.Delete(original);

        var data = TableImporter.Import(staged, null, SheetLayoutChoice.Auto);

        Assert.Equal("删得掉的.csv", Path.GetFileName(data.SourceFile));
        Assert.Equal(2, data.RowCount);
        Assert.Equal("144", data.Rows[0][1]);
    }

    [Fact]
    public void 退出时整批清掉()
    {
        ImportCache.Stage(WriteTable("要清的.csv"));
        Assert.True(Directory.Exists(ImportCache.Root));

        ImportCache.Clear();

        Assert.Empty(Directory.EnumerateFileSystemEntries(ImportCache.Root));
    }

    [Fact]
    public void 缓存目录写不成时退回读原件_不许把人卡在导入这一步()
    {
        // 只读盘 / 漫游配置没建好 / 杀软锁目录：这些情况下退回直接读原件，功能照旧，只是还会碰原件。
        var original = WriteTable("退路.csv");
        var realRoot = ImportCache.Root;
        try
        {
            UserPaths.SetRootForTests(Path.Combine(_dir, "一个文件不是目录"));
            File.WriteAllText(Path.Combine(_dir, "一个文件不是目录"), "x");

            var staged = ImportCache.Stage(original);

            Assert.False(staged.Copied);
            Assert.NotNull(staged.Note);
            Assert.Equal(Path.GetFullPath(original), staged.Path);
            var data = TableImporter.Import(staged.Path, null, SheetLayoutChoice.Auto);
            Assert.Equal(1, data.RowCount);
        }
        finally
        {
            UserPaths.SetRootForTests(realRoot);
        }
    }

    [Fact]
    public void 读表那几处不再用会挡住别人的共享模式()
    {
        // ZipFile.OpenRead 内部要的是 FileShare.Read = "我读的时候别人不许写"，
        // 而 WPS 打开一份表是读+写一起要的（它要拿编辑锁）→ 它报"被其他软件使用"。
        // 这两句源码判据拦的是"哪天有人把这两处改回去"。查的是调用形状，不是词——
        // 那两个文件自己的注释里就写着这两个 API 的名字。
        var reader = RepoFile("src", "LabelGou.Core", "Data", "XlsxTableReader.cs");
        Assert.DoesNotContain("ZipFile.OpenRead(filePath)", reader);
        Assert.Contains("FileShare.ReadWrite | FileShare.Delete", reader);

        var csv = RepoFile("src", "LabelGou.Core", "Data", "CsvTableReader.cs");
        Assert.DoesNotContain("File.ReadAllBytes(filePath)", csv);
        Assert.Contains("FileShare.ReadWrite | FileShare.Delete", csv);
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src"))) dir = Path.GetDirectoryName(dir);
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, Path.Combine(parts));
        return File.ReadAllText(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
