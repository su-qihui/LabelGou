using System.Text;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 「让 AI 读这张表并给一份提案」那条请求的提示词（第 21 棒）。
/// <para>放在 Core 而不是写在界面里，理由跟行式版式那段提示词（<c>RowLayoutPrompt</c>）一样：
/// 这段文字决定模型会不会回一份能解析的东西，它必须能被单测钉住，而不是藏在某个按钮的事件里。</para>
/// <para><strong>为什么现在才走这条路</strong>：用户 2026-09-09 的原话是
/// 「先让软件内的 AI 查看表格而不是根据程序写的自动来」+「把权限都给 AI，因为手动调整效果很差」。
/// 所以这里明确把三件事的裁量权交出去（表头行、合计行、整张纸），
/// 同时把软件能接受的形状写死（行号口径、纸规只能从清单里选、不许写任意毫米数）。</para>
/// </summary>
public static class AiSheetProposalPrompt
{
    /// <summary>系统那一句：只要方案，而且要说大白话（看的人是不懂技术的打印店老板）。</summary>
    public const string SystemText =
        "你是外贸纸箱唛头排版的现场工程师。你看得到整张表（含列名以上的批注、右侧贴的效果图）。" +
        "只输出一个 JSON 对象，不要解释文字、不要 Markdown 围栏。没把握的字段就省略，不要编。" +
        "读你回答的人是不懂电脑的打印店老板：warnings 与 reason 一律用中文大白话，" +
        "不要出现 rows、JSON、字段英文名、毫米坐标这类术语，也不要用技术报告的句式（写成「最后一行像合计，建议不印」而不是「第 N 行为 summary row」）。";

    /// <summary>
    /// 组装用户那一句。
    /// </summary>
    /// <param name="portrait">整张表的画像（含批注行与贴图清单），来自 <see cref="TablePortrait.Describe"/>。</param>
    /// <param name="sheetSpecNames">这台机器上真有的纸规名（内置 + 用户自建）。模型只能从这份清单里点名。</param>
    /// <param name="rawRowCount">原表总行数（1 起口径的边界）。</param>
    /// <param name="detectedHeaderRow">软件目前猜的表头行（1 起），0 表示还没切过。</param>
    /// <param name="currentLabelSizeText">当前模板尺寸那句人话（如「140×100 mm」），让模型知道改的是什么。</param>
    /// <param name="imageCount">这条消息随附几张图（0 时要它老实说没参照，不许凭列名编设计）。</param>
    public static string Build(
        string portrait,
        IReadOnlyList<string> sheetSpecNames,
        int rawRowCount,
        int detectedHeaderRow,
        string currentLabelSizeText,
        int imageCount)
    {
        var sb = new StringBuilder();
        sb.Append("任务：看下面这张表，给出「这张表该怎么切、这张纸该怎么摆」的提案。\n\n");
        sb.Append(portrait).Append('\n');
        sb.Append("\n原表一共 ").Append(rawRowCount).Append(" 行；软件目前把表头猜在第 ")
          .Append(detectedHeaderRow > 0 ? detectedHeaderRow.ToString() : "？").Append(" 行；当前标签尺寸 ")
          .Append(currentLabelSizeText).Append("。\n");
        sb.Append(imageCount > 0
            ? $"本条随附 {imageCount} 张图（表里贴的效果图/模板截图或用户拍的样张）：标签上印哪几行必须照图上的行序与字面，图上没有的行不要造。\n"
            : "本条没有随附图，也没有底稿：那就只报你在表里看到的事实（列名在哪行、哪几行不像货），标签内容那一段省略，不要凭列名编设计。\n");

        sb.Append("\n只回这样一个 JSON 对象（字段可省略，行号一律用**原表行号、从 1 起**，与人看 Excel 的口径一致）：\n");
        sb.Append("{\n");
        sb.Append("  \"hasHeader\": true 或 false,          // false = 这张表没有列名行，第一行也是货\n");
        sb.Append("  \"headerRow\": 1,                      // 列名在原表第几行\n");
        sb.Append("  \"totalRows\": [412],                  // 表尾「合计/TOTAL/小计」这类不该出标签的行，原表行号\n");
        sb.Append("  \"sheetSpec\": \"一页一枚（纸面跟标签走）\", // 只能从下面清单里原样选一个名字\n");
        sb.Append("  \"warnings\": [\"最后一行像合计，建议不印\"],  // 你看到的疑点：最多 5 条，一条一句、不超过 25 个中文，大白话\n");
        sb.Append("  \"reason\": \"为什么这么判（一句大白话，给老板看，不是给工程师看）\",\n");
        sb.Append("  \"rows\": [ { \"content\": \"Ctns No.{{CartonNo}}/{{CartonTotal}}\", \"sizePt\": 14, \"weight\": 1 } ]  // 行式版式；{{字段}} 用上面清单里的键，没连上的列写 {{col:表头原样}}\n");
        sb.Append("}\n");

        if (sheetSpecNames.Count > 0)
        {
            sb.Append("\n纸规清单（只能选这些名字，写别的会被拒）：\n");
            foreach (var name in sheetSpecNames.Take(20)) sb.Append("  - ").Append(name).Append('\n');
        }
        sb.Append("\n字段清单（rows 里能用的 {{键}}）：\n  ");
        sb.Append(string.Join(" ", MarkFieldCatalog.Mappable.Select(d => d.Key))).Append('\n');
        sb.Append("硬性约束：行号必须在 1~").Append(rawRowCount).Append(" 之间；列名那一行不能同时被列进 totalRows；")
          .Append("不要输出毫米坐标、不要改纸张几何（只点名用哪张纸）；看不清就说看不清，宁可省略字段。\n");
        sb.Append("一律用中文。");
        return sb.ToString();
    }
}
