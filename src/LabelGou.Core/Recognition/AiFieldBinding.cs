using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>
/// AI 报的一条**字段绑定**（第 30 棒）：哪一列是哪个唛头字段。
/// <para><strong>能出现在这里，就说明它已经被校验过</strong>：字段命中 19 个标准字段白名单、列能对回真表头
/// （见 <c>AiSheetProposal.ParseMappings</c>）。解析不过的一律只留一句 Note，**绝不猜**——
/// 猜错一列就是数错张数、印错货，与 <c>AiSheetProposal.ResolveColumn</c> 是同一条口径。</para>
/// </summary>
/// <param name="Field">命中白名单的唛头字段。</param>
/// <param name="ColumnIndex">列下标（0 起，已对回这张表真有的列）。</param>
/// <param name="ColumnHeader">列标题原文——给人看用它，列序变了也靠它重连（与 <c>FieldMapping.ColumnHeader</c> 同一套语义）。</param>
/// <param name="Reason">它说为什么这么判（可空，一句大白话，卡片上不显示、留作解释用）。</param>
public sealed record AiFieldBinding(
    MarkFieldKey Field,
    int ColumnIndex,
    string ColumnHeader,
    string? Reason = null)
{
    /// <summary>字段的中文名（改动卡上"改哪里"那一行用它）。</summary>
    public string FieldName => MarkFieldCatalog.Get(Field).ChineseName;
}
