using LabelGou.Core.Marks;

namespace LabelGou.Core.Recognition;

/// <summary>文本从哪条通道来。M6 的交叉校验（D11）靠它区分来源，不靠猜。</summary>
public enum TextChannel
{
    /// <summary>本地 OCR（Windows 内置识别引擎）：快、离线、零依赖，但符号、小数、词内空格会脏。</summary>
    Ocr = 0,

    /// <summary>文档自带文字层（<c>.docx</c> 正文）：文字本身准，但版面顺序与分栏会丢。</summary>
    DocxText = 1,

    /// <summary>多模态大模型直接给的字段值（不产文本行，只产候选值）。</summary>
    Llm = 2,
}

/// <summary>
/// 一行识别出来的文字 + 它在图里的位置。
/// <para>像素坐标只用于一件事：核对窗口里让用户能点一下就知道原图哪一块对应这个值。
/// 不参与任何排版计算——印刷几何仍以毫米为唯一真源（§七-13）。</para>
/// </summary>
public sealed class TextLine
{
    public required string Text { get; init; }

    public int PixelX { get; init; }
    public int PixelY { get; init; }
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }

    public override string ToString() => Text;
}

/// <summary>
/// 一份文档的文本层（OCR 或 docx 直读的结果）。它是 D12 里那个「证据池」的载体：
/// 大模型给的每个值都必须能在这里找到支撑，否则一律标红待核。
/// </summary>
public sealed class RecognizedText
{
    public required TextChannel Channel { get; init; }

    /// <summary>来源文件名或路径简述，核对窗口与日志都要显示它。</summary>
    public required string SourceName { get; init; }

    public List<TextLine> Lines { get; } = new();

    /// <summary>识别过程中发现的人话告警（没装语言包、图太大被缩、行数超限等）。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>通道是否真跑出了东西（docx 只有图片时也算空）。</summary>
    public bool IsEmpty => Lines.Count == 0;

    public int LineCount => Lines.Count;

    /// <summary>整篇文本，按行拼接。日志与诊断用，不参与匹配。</summary>
    public string FullText => string.Join("\n", Lines.Select(l => l.Text));

    public void AddLine(string? text, int x = 0, int y = 0, int w = 0, int h = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Lines.Add(new TextLine
        {
            Text = text,
            PixelX = x,
            PixelY = y,
            PixelWidth = w,
            PixelHeight = h,
        });
    }

    /// <summary>便捷构造：只有文本行、没有坐标（docx 与单测夹具用）。</summary>
    public static RecognizedText FromLines(TextChannel channel, string sourceName, IEnumerable<string> lines)
    {
        var text = new RecognizedText { Channel = channel, SourceName = sourceName };
        foreach (var line in lines) text.AddLine(line);
        return text;
    }
}

/// <summary>
/// 某通道给出的一个「字段候选值」——还没经过交叉校验，所以不叫结果。
/// <para>规则抽取与 LLM 解析都产这个类型，方便 <see cref="CrossValidator"/> 一视同仁地合并。</para>
/// </summary>
public sealed class FieldCandidate
{
    public required MarkFieldKey Field { get; init; }

    /// <summary>原始文本（未规范化），核对时要给用户看原样，不能只给改过的。</summary>
    public required string RawValue { get; init; }

    public required ValueOrigin Origin { get; init; }

    /// <summary>支撑这个值的文本行下标；-1 表示没有文本层证据（LLM 独有值）。</summary>
    public int EvidenceLineIndex { get; init; } = -1;

    /// <summary>证据行原文快照（下标会变，所以自带一份）。</summary>
    public string Evidence { get; init; } = string.Empty;

    /// <summary>0~1，通道自身的把握（规则命中强度、别名长度等），不含交叉校验结论。</summary>
    public double Confidence { get; init; }

    /// <summary>通道给出的附加说明，会并进核对窗口的提示列。</summary>
    public string? Note { get; set; }

    public override string ToString() => $"{Field}: {RawValue} ({Origin}, {Confidence:F2})";
}
