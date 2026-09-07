using System.Text;
using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 一份文档识别 + 交叉校验的完整结果，以及它的核对状态。
/// <para>这一层是 M6 与 M1 主干的接缝：产出的仍是 <see cref="MarkRecord"/>，
/// 于是 M1 已有的「待核字段标红 + 未确认不打印」闸门（<see cref="MarkValue.NeedsReview"/> →
/// <c>MarkRecord.PendingReview</c> → 打印/导出前的 <c>HasUnconfirmed</c>）原样生效，不新建第二套核对体系。</para>
/// </summary>
public sealed class RecognizedBatch
{
    /// <summary>一份文档一次识别，字段数上限（唛头 19 个字段，超了就是异常）。</summary>
    public const int MaxFields = 32;

    public required string SourceName { get; init; }

    /// <summary>来源文件的完整路径（核对窗口要拿它显示原图）。内存构造/单测里可以为空。</summary>
    public string? SourcePath { get; init; }

    /// <summary>文本层本身，核对时要点开看原文。只是 <see cref="TextLine"> 组合，不含任何 IO 类型。</summary>
    public RecognizedText? Text { get; set; }

    /// <summary>文本层来自哪条路（OCR / docx / 都没有），界面要如实说出来。</summary>
    public TextChannel TextChannel { get; set; } = TextChannel.Ocr;

    public List<ReviewedField> Fields { get; } = new();

    /// <summary>通道级告警（引擎不可用、模型超时、行数超限……），与字段级告警分开存。</summary>
    public List<string> ChannelWarnings { get; } = new();

    /// <summary>模型返回的原始 JSON，留一份便于事后追查它到底说了什么。</summary>
    public string? RawModelPayload { get; set; }

    /// <summary>还差多少人没确认（空值不算待核，本来就没这东西）。</summary>
    public int PendingCount => Fields.Count(f => f.IsPending && !string.IsNullOrWhiteSpace(f.Value));

    /// <summary>需要特别看一眼的行数：不一致、无证据或有告警。</summary>
    public int HotCount => Fields.Count(f => f.IsHot && !string.IsNullOrWhiteSpace(f.Value));

    public bool ReadyToImport => Fields.Count > 0 && PendingCount == 0;

    /// <summary>
    /// 批量确认「机器真有把握」的行：两通道都开了口、给出同一个值、证据是原文级、且没有任何告警。
    /// <para>有告警或没证据的行必须逐条点，不给一键放行——这是 §五-9 那条红线在界面上的落点。</para>
    /// <para><strong>为什么要求两路都有值</strong>：<c>Agreed</c> 只表示“没发现分歧”，只有一路说话时它永远是真。
    /// 本机真样本里，规则通道把客户代码读成了 “MACYS ( 0 NTRACT”（OCR 掉了字导致 CONTRACT 没被锚住），
    /// 它在原文里确实存在、所以证据是 Exact，单靠“一致 + 原文命中”就会被自动放行 —— 而这正是要印错的值。</para>
    /// </summary>
    /// <returns>本次被批量确认的行数。</returns>
    public int BulkConfirm()
    {
        var count = 0;
        foreach (var field in Fields)
        {
            if (field.Confirmed) continue;
            if (string.IsNullOrWhiteSpace(field.Value)) continue;
            if (!field.Agreed || field.Warning is not null) continue;
            if (field.Evidence is not (EvidenceLevel.Exact or EvidenceLevel.Compact)) continue;
            if (string.IsNullOrWhiteSpace(field.TextValue) || string.IsNullOrWhiteSpace(field.LlmValue)) continue;

            field.Confirmed = true;
            count++;
        }

        return count;
    }

    /// <summary>进度简述，直接进状态栏，不再另造一套文案。</summary>
    public string DescribeProgress()
    {
        var sb = new StringBuilder();
        sb.Append($"共 {Fields.Count} 个字段，待核 {PendingCount} 项");
        if (HotCount > 0) sb.Append($"，其中 {HotCount} 项需要特别看一眼");
        if (ReadyToImport) sb.Append("，全部已确认，可以导入");
        return sb.ToString();
    }

    /// <summary>
    /// 转成一条唛头记录。未确认的字段<strong>照样写进去</strong>并带上 <see cref="MarkValue.NeedsReview"/>：
    /// 这样预览会标红、打印会被闸门拦下，而不是"看不见"——用户要能发现自己还有没核的东西。
    /// </summary>
    public MarkRecord ToRecord(int rowIndex)
    {
        var builder = MarkRecord.Builder().SetRow(rowIndex, $"识别自 {SourceName}");
        foreach (var field in Fields)
        {
            var text = (field.Value ?? string.Empty).Trim();
            if (text.Length == 0) continue;

            var sourceRef = field.EvidenceLineIndex >= 0
                ? $"{SourceName} 第 {field.EvidenceLineIndex + 1} 行「{field.EvidenceText}」"
                : $"{SourceName} · 模型给出，无文本层证据";

            builder.Set(field.Field, new MarkValue(text, field.Origin)
            {
                Confidence = field.Confidence,
                NeedsReview = !field.Confirmed,
                Warning = field.Warning ?? (field.Confirmed ? null : "未人工核对"),
                SourceRef = sourceRef,
            });
        }

        return builder.Build();
    }
}
