using System.Globalization;
using System.Text.RegularExpressions;
using LabelGou.Core.Data;
using LabelGou.Core.Impos;

namespace LabelGou.App.Services;

/// <summary>
/// 表里那些"给人看的话"中藏着的**纸规线索**（第 101 棒）。
/// <para>用户 2026-09-24 的口径是「<strong>看到就修改</strong>」——认到了就直接改纸规，不是提示一句等他动手。
/// 他点名的两个地方：<strong>一般在列头</strong>（「一开四」「一开八」这类开法行话）、
/// <strong>或在模板边</strong>写着的一组尺寸（「30*40」这种）；<strong>例外当场给了一条：
/// 「开二」一般默认为小一开二</strong>，所以裸的「开二」/「一开二」都落到小开二那一档，不是大开二。</para>
/// <para><strong>两种线索的取法不一样，这是刻意的</strong>：行话落在哪个格子里都是指令（一格写着「一开四」
/// 不会是货的值），所以列头、表头上方的批注、数据区都扫；而一组 <c>数字*数字</c> 落在数据区里更可能是
/// 体积或尺寸真值，<strong>那种不能拿来改纸规</strong>（"不猜"那道闸），所以尺寸只认列头与批注。</para>
/// </summary>
public static class SheetSpecHints
{
    /// <summary>认到的一条线索。<see cref="SpecId"/> 为 null = 认到了写法但对不上任何一档（只回显，不改）。</summary>
    public sealed class Found
    {
        public string? SpecId { get; init; }

        /// <summary>原话（截断过）：改完要让他看得见"是照哪句话改的"，不许静默改他的纸。</summary>
        public string Evidence { get; init; } = "";

        public bool MatchesSpec => SpecId is not null;
    }

    /// <summary>
    /// 行话 → 纸规档。<strong>长的必须排在前面</strong>：「大开二」「小开二」「一开二」都含裸「开二」，
    /// 谁先被扫到就归谁（§五-185 同一族：顺序就是判据，红检靠"把裸词提到最前"应当变红来钉）。
    /// </summary>
    private static readonly (string Word, string SpecId)[] Jargon =
    {
        ("小一开二", BuiltInSheetSpecs.IdSmall2_160x240),
        ("一开四", BuiltInSheetSpecs.IdCut4_280x200),
        ("一开八", BuiltInSheetSpecs.IdCut8_280x200),
        ("小开二", BuiltInSheetSpecs.IdSmall2_160x240),
        ("大开二", BuiltInSheetSpecs.IdBig2_280x200),
        // 他给的例外：光说「开二」默认就是小一开二；「一开二」同族，一起归这一档。
        ("一开二", BuiltInSheetSpecs.IdSmall2_160x240),
        ("开二", BuiltInSheetSpecs.IdSmall2_160x240),
    };

    /// <summary>一组尺寸：<c>30*40</c> / <c>28×20</c> / <c>16x24</c> 都算（厂里人打星号，也打乘号与字母 x）。</summary>
    private static readonly Regex SizePattern = new(
        @"(\d{1,3}(?:\.\d+)?)\s*[*×xX]\s*(\d{1,3}(?:\.\d+)?)", RegexOptions.Compiled);

    /// <summary>
    /// 从一句话里认线索。认不到返回 null。
    /// <c>sizesToo=false</c> 只认行话——扫数据区时用它：那里的 <c>数字*数字</c> 多半是货的体积，不是纸规。
    /// </summary>
    public static Found? Detect(string? text, bool sizesToo = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (var (word, specId) in Jargon)
            if (text.Contains(word, StringComparison.Ordinal))
                return new Found { SpecId = specId, Evidence = Clip(text) };

        if (!sizesToo) return null;
        var match = SizePattern.Match(text);
        if (!match.Success) return null;

        var a = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var b = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        // 「28*20」这种厂里话是**厘米**（纸规名字里就写着「28×20 纸」= 280×200mm），所以先按厘米试；
        // 试不着再按毫米试——万一人家写的本来就是 mm。两样都对不上就只回显，不猜一档出来。
        var hit = MatchPaper(a * 10, b * 10) ?? MatchPaper(a, b);
        return new Found { SpecId = hit, Evidence = Clip(text) };
    }

    /// <summary>
    /// 扫一张表：列头 → 表头上方的批注（这两种地方行话与尺寸都认）→ 数据区（只认行话）。
    /// 先认到的那条说话，后面的不再翻。
    /// </summary>
    public static Found? DetectInTable(TabularData data)
    {
        foreach (var header in data.Headers)
            if (Detect(header) is { } byHeader) return byHeader;

        foreach (var line in data.Preamble)
            foreach (var cell in line)
                if (Detect(cell) is { } byNote) return byNote;

        foreach (var row in data.Rows)
            foreach (var cell in row)
                if (Detect(cell, sizesToo: false) is { } byWord) return byWord;

        return null;
    }

    private static string? MatchPaper(double w, double h)
        => BuiltInSheetSpecs.All().FirstOrDefault(s =>
            (Nearly(s.PaperWidthMm, w) && Nearly(s.PaperHeightMm, h)) ||
            (Nearly(s.PaperWidthMm, h) && Nearly(s.PaperHeightMm, w)))?.Id;

    /// <summary>与 <c>SheetSpec</c> 同一把尺子：0.6mm 以内就是同一张刀模。</summary>
    private static bool Nearly(double a, double b) => Math.Abs(a - b) < 0.6;

    private static string Clip(string text)
    {
        var oneLine = text.Trim().Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', ' ');
        return oneLine.Length <= 40 ? oneLine : oneLine[..40] + "…";
    }
}
