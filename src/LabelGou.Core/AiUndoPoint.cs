using LabelGou.Core.Data;
using LabelGou.Core.Marks;

namespace LabelGou.Core;

/// <summary>
/// 「AI 动手之前」的一份快照（第 33 棒）：**逐步撤回的依据**。
/// <para><strong>为什么要有它</strong>：用户 2026-09-10 把交互架构改成了
/// 「输入表格 → AI 思考 → 只列出要他拍板的问题 → 选择后回答注入思考 → **直接注入预览**（不再逐条点头）
/// → 右侧 AI 栏**可撤回**」，撤回粒度他选的是**逐步**（能连着往回退好几步）。
/// 那"一步"是什么，得先说清：<strong>一次 AI 动手 = 一步</strong>——读表提案落一次是一步，
/// 他答一条问题落一次也是一步。所以快照压的是"这一次动手之前的样子"。</para>
/// <para><strong>安全模型换方向了</strong>（写在这里免得以后有人以为是漏了红线）：
/// 阶段 29 定的是"**事前拦**：每次写都要人点 ✅ 才落地、不许静默落地"；
/// 第 33 棒起改成"**事后可撤销**：能自动判的直接生效进预览，靠人看得见 + 能撤回兜底"。
/// **打印前那道复核闸门（⑤）不动**——那是最后一道，与这个改动无关。</para>
/// <para>只装"AI 能写的那几样"（数据模型层：切法 / 绑定 / 模板 / 纸规），
/// 与 §七-10 那条边界一致；**毫米落位与字体度量不在其中**（那些仍归引擎，AI 从没碰过）。</para>
/// </summary>
/// <param name="Label">这一步是什么（给人看，如「读表提案」/「你拍的那条」）。</param>
/// <param name="Choice">切法（列名行 / 剔行 / 合计行兜底）。</param>
/// <param name="Bindings">字段 → 列下标（没绑的字段不在里面）。</param>
/// <param name="TemplateId">当时选中的模板 Id（null = 当时没选中）。</param>
/// <param name="SheetSpecName">当时用的纸规名（null = 当时没选中）。</param>
/// <param name="At">什么时候压的。</param>
public sealed record AiUndoPoint(
    string Label,
    SheetLayoutChoice Choice,
    IReadOnlyDictionary<MarkFieldKey, int> Bindings,
    string? TemplateId,
    string? SheetSpecName,
    DateTime At)
{
    /// <summary>撤回按钮上那句话（说清"退回哪一步"）。</summary>
    public string Describe() => $"撤回「{Label}」这一步";
}
