using System.Windows.Media;
using LabelGou.Core.Templates;

namespace LabelGou.App.Services;

/// <summary>
/// 本机装了哪些字、没装时替成哪个（第 88 棒：从 CorelDRAW 逐对象导入用）。
/// <para>Corel 稿子上的字体店里常常没装（实测金沐用的是 Adobe Gothic Std B，这台机器就没有）。
/// 用户 2026-09-19 的口径：<strong>没装就先拿一个能正常显示的字替</strong>，别掉成方框或乱换字身——
/// 替代关系要写进元素的来源留痕，让人看得见"这一行不是原字"。</para>
/// </summary>
public static class FontSubstitution
{
    static readonly Lazy<HashSet<string>> Installed = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
            if (!string.IsNullOrWhiteSpace(family.Source)) names.Add(family.Source);
        names.Add(TemplateElement.DefaultFont);
        return names;
    });

    /// <summary>本机有没有这个字族（大小写不敏感）。</summary>
    public static bool IsInstalled(string family) => !string.IsNullOrWhiteSpace(family) && Installed.Value.Contains(family);

    /// <summary>
    /// 要替就返回替成谁，不用替返回 null。
    /// <para>只替到默认字体（微软雅黑：Win10/11 自带、无衬线、中英混排不掉字，和唛头常用的黑体同一路），
    /// 不去猜"Adobe Gothic 的等价字是哪支"——猜出来的等价字一样不是原字身，却会让人以为已经准了。</para>
    /// </summary>
    public static string? Resolve(string requestedFamily) =>
        IsInstalled(requestedFamily) ? null : TemplateElement.DefaultFont;
}
