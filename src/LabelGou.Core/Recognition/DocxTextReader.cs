using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace LabelGou.Core.Recognition;

/// <summary>
/// 直读 <c>.docx</c> 正文文字（零依赖：<see cref="ZipArchive"/> + <see cref="XDocument"/>）。
/// <para>为什么不走 OCR：.docx 本身就是 zip + WordprocessingML，文字是准的，不需要识别。
/// 工厂发来的"唛头 Word 文档"这类件型，走这条路比贴图识别可靠得多，也快得多（毫秒级 vs 34 秒）。</para>
/// <para>代价：只取段落与表格文字，<strong>表格按行摊平（一行的各列合成一行、列间一个空格）、图片不取、版面位置全丢</strong>。
/// 文本框里的字照取，且只取一次：<c>w:p</c> 套 <c>w:txbxContent</c> 套 <c>w:p</c> 是嵌套关系，
/// 上一版用 <c>Descendants("w:p")</c> 平铺，同一段文本框文字会被宿主段落吞一遍、自己再出
/// 一遍，而 <c>mc:AlternateContent</c> 的回退副本（<c>mc:Fallback</c>）还要再算第三遍。
/// 所以它给出的文本行序不一定等于人眼读的顺序——交叉校验时它的证据强度与 OCR 同级，不多给分。</para>
/// </summary>
public static class DocxTextReader
{
    private const string WordNamespace =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>markup-compatibility 命名空间：图文混排时 Word 会把同一份内容写两遍（Choice + Fallback）。</summary>
    private const string CompatNamespace =
        "http://schemas.openxmlformats.org/markup-compatibility/2006";

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
        var mc = XNamespace.Get(CompatNamespace);
        var count = 0;

        // 按文档序走一遍：表格行整体出（一行一条），其余段落各自出一条。
        // 已经归某个表格行的段落不再单独出，否则「G.W.」和「25.5 KGS」会变成互不相干的两行，规则抽不出值。
        foreach (var node in doc.Descendants())
        {
            if (IsInFallback(node, mc)) continue;

            IReadOnlyList<string> lines;
            if (node.Name == w + "tr")
            {
                if (node.Ancestors(w + "tr").Any()) continue;         // 嵌套表的行由外层那一行一起算
                lines = RowLines(node, w);
            }
            else if (node.Name == w + "p")
            {
                if (node.Ancestors(w + "tr").Any()) continue;         // 表格里的段落归行管
                var own = ParagraphText(node, w);
                lines = own.Length == 0 ? Array.Empty<string>() : new[] { own };
            }
            else continue;

            foreach (var line in lines)
            {
                if (count >= MaxParagraphs)
                {
                    text.Warnings.Add($"段落数超过 {MaxParagraphs}，后面的没读（多半不是唛头单页文档）。");
                    return text;
                }
                text.AddLine(line);
                count++;
            }
        }

        if (text.IsEmpty) text.Warnings.Add("这份 Word 文档里没有可读文字（可能整页都是图片，改走图片识别）。");
        return text;
    }

    /// <summary>是否在 <c>mc:Fallback</c> 分支里——那一支是同一份内容的副本，读一遍就会重复。</summary>
    private static bool IsInFallback(XElement node, XNamespace mc)
    {
        for (var a = node.Parent; a is not null; a = a.Parent)
        {
            if (a.Name == mc + "Fallback") return true;
        }
        return false;
    }

    /// <summary>把一行的各列拼成行：列与列之间一个空格；单元格里有几段，就按段序对齐成几行（段数不够的补空）。
    /// <para>为什么按行拼而不是逐格摊平：两列表格（左列标签、右列值）摊平后标签与值分家，
    /// <see cref="RuleFieldExtractor"/> 是靠「同一行里锚住标签取后面的值」干活的，分家等于抽不出。</para></summary>
    private static IReadOnlyList<string> RowLines(XElement row, XNamespace w)
    {
        var cells = row.Elements(w + "tc").ToList();
        var cellLines = cells
            .Select(tc => tc.Descendants(w + "p")
                .Where(p => p.Ancestors(w + "tr").All(tr => tr == row))   // 嵌套表的段落不算这一格
                .Select(p => ParagraphText(p, w))
                .Where(s => s.Length > 0)
                .ToList())
            .Where(list => list.Count > 0)
            .ToList();
        if (cellLines.Count == 0) return Array.Empty<string>();

        var height = cellLines.Max(list => list.Count);
        var lines = new List<string>(height);
        for (var i = 0; i < height; i++)
        {
            var joined = string.Join(" ", cellLines.Select(list => i < list.Count ? list[i] : string.Empty)
                .Where(s => s.Length > 0));
            if (joined.Length > 0) lines.Add(joined);
        }
        return lines;
    }

    /// <summary>
    /// 拼一个段落自己的文字：<c>w:t</c> 是文本，<c>w:tab</c> 是空格，<c>w:br</c>/<c>w:cr</c> 视为分段断点。
    /// <para>刻意不按 <c>w:r</c> 的样式加空格——run 边界随手插空格会让 "G.W." 变成 "G. W."，
    /// 反而制造 OCR 那类噪声。</para>
    /// <para>只算本段自己的 <c>w:t</c>：文本框里的段落是嵌在本段下面的，它们会自己出一条，
    /// 这里再吞进来就是一份内容两条线。</para>
    /// </summary>
    private static string ParagraphText(XElement paragraph, XNamespace w)
    {
        var sb = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (BelongsToNestedParagraph(node, paragraph, w)) continue;

            if (node.Name == w + "t") sb.Append(node.Value);
            else if (node.Name == w + "tab") sb.Append(' ');
            else if (node.Name == w + "br" || node.Name == w + "cr") sb.Append('\n');
        }

        // 段内换行按 Word 的软回车处理成独立行：调用方只吃"一行一条"，这里就地拆开。
        var lines = sb.ToString().Split('\n');
        var kept = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(TextNormalizer.Squeeze);
        return string.Join(" ", kept);
    }

    /// <summary>往上走到本段为止，中途碰到另一个 <c>w:p</c> 就说明这段文字是文本框（或嵌套内容）的，不归本段。</summary>
    private static bool BelongsToNestedParagraph(XElement node, XElement owner, XNamespace w)
    {
        for (var a = node.Parent; a is not null && a != owner; a = a.Parent)
        {
            if (a.Name == w + "p") return true;
        }
        return false;
    }
}
