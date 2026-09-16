using System;
using System.IO;

namespace LabelGou.Core;

/// <summary>
/// 用户可写目录的<strong>唯一</strong>来源：模板库、纸规库、映射方案库都从这里取子目录。
/// <para>
/// 为什么单独抽这一个类：上一版三处各自写 <c>Path.Combine(ApplicationData, "LabelGou", …)</c>，
/// 单测没有任何口子改它，于是 <c>new MainViewModel()</c> 会去读<strong>用户真机上那个模板库</strong>。
/// 2026-09-08 实测踩到：用户自己用 AI 排过一版并存成用户模板之后，三条既有单测里
/// 「自动接手该挑哪个模板」的期望当场变了——测试没坏，是它偷看了人家的私人目录。
/// </para>
/// <para>
/// 这与 §五-48 是同一件事（那次只把日志与 <c>uistate.json</c> 接进临时目录），
/// 现在把剩下三个会读真实目录的库一并接走。
/// </para>
/// </summary>
public static class UserPaths
{
    private static string? _rootOverride;

    /// <summary>数据根目录，正常是 <c>%APPDATA%\LabelGou</c>；单测用 <see cref="SetRootForTests"/> 换掉。</summary>
    public static string Root => _rootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabelGou");

    /// <summary>
    /// 只给单测用：把整个数据根目录改到临时目录，一次覆盖模板/纸规/映射方案三个库。
    /// <para>故意不提供"改回来"的口子——它在 <c>ModuleInitializer</c> 里跑一次，早于任何测试代码，
    /// 中途改回去只会让并行跑的测试类看到两套目录，那比现在这个问题更难查。</para>
    /// </summary>
    public static void SetRootForTests(string directory)
    {
        Net6Compat.ThrowIfNullOrEmpty(directory);
        _rootOverride = directory;
    }
}
