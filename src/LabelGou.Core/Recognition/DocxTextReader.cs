using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 直读 <c>.docx</c> 正文文字（零依赖：<see cref="ZipArchive"/> + <see cref="XDocument"/>）。
/// <para>为什么不走 OCR：.docx 本身就是 zip + WordprocessingML，文字是准的，不需要识别。
/// 工厂发来的"唛头 Word 文档"这类件型，走这条路比贴图识别可靠得多，也快得多（毫秒级 vs 34 秒）。</para>
/// <para>代价：只取段落文字，<strong>表格按单元格顺序摊平成行、图片与文本框内容不取、版面位置全丢</strong>。
/// 所以它给出的文本行序不一定等于人眼读的顺序——交叉校验时它的证据强度与 OCR 同级，不多给分。</para>
/// </summary>
public static class DocxTextReader
{
    private const string WordNamespace =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>段落上限，防呆（一份正常唛头文档远用不到）。超过即视为异常并要求人工改用其它通道。</summary>
    public const int MaxParagraphs = 2000;

    public static RecognizedText ReadFile(string path)
    {
        var text = new RecognizedText { Channel = TextChannel.DocxText, SourceName = Path.GetFileName(path) };

        XDocument doc;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null)
            {
                text.Warnings.Add("这个 .docx 里找不到 word/document.xml，不是有效的 Word 文档。");
                return text;
            }

            using var stream = entry.Open();
            doc = XDocument.Load(stream);
        }
        catch (InvalidDataException)
        {
            text.Warnings.Add("这个 .docx 打不开（zip 结构损坏，或其实是老式 .doc 改名）。");
            return text;
        }
        catch (Exception ex)
        {
            text.Warnings.Add($"读 Word 文档失败：{ex.Message}");
            return text;
        }

        var w = XNamespace.Get(WordNamespace);
        var count = 0;
        foreach (var paragraph in doc.Descendants(w + "p"))
        {
            if (count >= MaxParagraphs)
            {
                text.Warnings.Add($"段落数超过 {MaxParagraphs}，后面的没读（多半不是唛头单页文档）。");
                break;
            }

            var line = ParagraphText(paragraph, w);
            if (line.Length == 0) continue;
            text.AddLine(line);
            count++;
        }

        if (text.IsEmpty) text.Warnings.Add("这份 Word 文档里没有可读文字（可能整页都是图片，改走图片识别）。");
        return text;
    }

    /// <summary>
    /// 拼一个段落的全部文字：<c>w:t</c> 是文本，<c>w:tab</c> 是空格，<c>w:br</c>/<c>w:cr</c> 视为分段断点。
    /// <para>刻意不按 <c>w:r</c> 的样式加空格——run 边界随手插空格会让 "G.W." 变成 "G. W."，
    /// 反而制造 OCR 那类噪声。</para>
    /// </summary>
    private static string ParagraphText(XElement paragraph, XNamespace w)
    {
        var sb = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == w + "t") sb.Append(node.Value);
            else if (node.Name == w + "tab") sb.Append(' ');
            else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append('\n');
        }

        // 段内换行按 Word 的软回车处理成独立行：调用方只吃"一行一条"，这里就地拆开。
        var lines = sb.ToString().Split('\n');
        var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(TextNormalizer.Squeeze);
        return string.Join(" ", kept);
    }
}
