using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LabelGou.Core.Templates;

namespace LabelGou.App.Services;

/// <summary>
/// 「删模板」这件事的**安全做法**（用户 2026-09-10 提的：模板列表里堆了一大堆同名条目，
/// 他想删但先问了一句「那如果有用的模版我也删了吗??」）。
/// <para><strong>为什么单独抽一层而不是写在按钮回调里</strong>：删除是**不可逆**的动作，
/// 而要删的名单来自界面勾选——写在窗口里就只能靠"点一遍看看"来验；
/// 抽出来就能拿临时目录**真删一次**来钉住：备份到底建没建、内置到底动没动、没勾到底删不删。</para>
/// <para>口径三条：</para>
/// <list type="number">
/// <item>**内置模板永不删**（<see cref="TemplateStore.Delete"/> 本来就拒；这里也不让它进备份名单）；</item>
/// <item>**删之前先把文件复制到备份目录**（<c>_已删除_&lt;时间戳&gt;</c>），删完把路径告诉用户——
/// 这是"万一删错了"的唯一补救（用户点名担心的就是这件事）；</item>
/// <item>一份可删的都没有时，**不建空目录、不碰任何文件**。</item>
/// </list>
/// </summary>
public static class TemplateCleanup
{
    /// <summary>按 Id 批量删除，**先备份再删**。</summary>
    /// <param name="store">模板库。</param>
    /// <param name="ids">要删的 Id（重复的自动去掉）。</param>
    /// <returns>
    /// <c>Deleted</c> 真删掉几份；<c>Skipped</c> 没删成几份（内置 / 文件已不在）；
    /// <c>BackupDir</c> 备份目录（一份都没删时为 null——不留空目录）。
    /// </returns>
    public static (int Deleted, int Skipped, string? BackupDir) DeleteWithBackup(
        TemplateStore store, IReadOnlyList<string> ids)
    {
        var targets = new List<(string Id, string File)>();
        var skipped = 0;
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            // FindFileFor 对内置模板返回 null（它不落盘），所以内置连备份名单都进不来。
            var file = store.FindFileFor(id);
            if (file is null)
            {
                skipped++;
                continue;
            }
            targets.Add((id, file));
        }
        if (targets.Count == 0) return (0, skipped, null);

        // 备份目录放在 templates/ 下的子目录：库只枚举顶层 *.json，所以备份不会重新出现在模板列表里。
        var backupDir = Path.Combine(store.UserDirectory, $"_已删除_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(backupDir);
        foreach (var (_, file) in targets)
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(backupDir, name);
            for (var n = 2; File.Exists(dest); n++)     // 同名（同一秒删两份同名模板）不许互相覆盖
                dest = Path.Combine(backupDir, Path.GetFileNameWithoutExtension(name) + $"_{n}.json");
            File.Copy(file, dest, overwrite: false);
        }

        var deleted = 0;
        foreach (var (id, _) in targets)
            if (store.Delete(id)) deleted++;
        return (deleted, skipped, backupDir);
    }
}
