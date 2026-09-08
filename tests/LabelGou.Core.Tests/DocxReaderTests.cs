using System.IO.Compression;
using System.Text;
using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// Word 正文直读（<see cref="DocxTextReader"/>）的单测。
/// <para>这条通道此前一条直接测试都没有：只在 App 侧的识别流程测试里被顺路走了一遍，
/// 而那份夹具全是「一段一行」的干净正文，正好绕开了它真正会栽的两个坑——
/// <strong>两列表格</strong>（左列标签右列值）和<strong>文本框</strong>（Word 把内容写成嵌套段落 + 回退副本）。</para>
/// <para>表格摊平成逐格一行时，「G.W.」与「25.5 KGS」分家，<see cref="RuleFieldExtractor"/> 就抽不出值了；
/// 文本框用 <c>Descendants("w:p")</c> 平铺时，同一段字会被宿主段落吞一遍、自己再出一遍，
/// <c>mc:Fallback</c> 那份副本还要再算一遍——一份内容三条线，证据池里就成了重复证据。</para>
/// </summary>
public class DocxReaderTests
{
    private const string DocxNamespaceDeclarations =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\"";

    /// <summary>把任意 body 内容包成一份 .docx（zip 里只有 word/document.xml 就够这个读取器用了）。</summary>
    private static string WriteDocx(string body, string name = "单据.docx")
    {
        var path = Path.Combine(Path.GetTempPath(), $"labelgou-docx-{Guid.NewGuid():N}.docx");
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<w:document {DocxNamespaceDeclarations}><w:body>{body}</w:body></w:document>";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
        return path;
    }

    private static string[] Read(string body)
    {
        var path = WriteDocx(body);
        try
        {
            return DocxTextReader.ReadFile(path).Lines.Select(l => l.Text).ToArray();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Paragraph(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";

    // ---------- 表格：一行必须是一行 ----------

    private const string TwoColumnTable = """
        <w:tbl>
          <w:tr>
            <w:tc><w:p><w:r><w:t>G.W.</w:t></w:r></w:p></w:tc>
            <w:tc><w:p><w:r><w:t>25.5 KGS</w:t></w:r></w:p></w:tc>
          </w:tr>
          <w:tr>
            <w:tc><w:p><w:r><w:t>N.W.</w:t></w:r></w:p></w:tc>
            <w:tc><w:p><w:r><w:t>22.1 KGS</w:t></w:r></w:p></w:tc>
          </w:tr>
        </w:tbl>
        """;

    [Fact]
    public void 两列表格的一行会被合成一行()
    {
        var lines = Read(TwoColumnTable);

        Assert.Equal(new[] { "G.W. 25.5 KGS", "N.W. 22.1 KGS" }, lines);
    }

    [Fact]
    public void 两列表格的重量真能被规则抽出来()
    {
        var path = WriteDocx(TwoColumnTable);
        try
        {
            var found = RuleFieldExtractor.Extract(DocxTextReader.ReadFile(path)).Candidates;

            var gross = Assert.Single(found, c => c.Field == MarkFieldKey.GrossWeight);
            Assert.Equal("25.5 KGS", gross.RawValue);
            var net = Assert.Single(found, c => c.Field == MarkFieldKey.NetWeight);
            Assert.Equal("22.1 KGS", net.RawValue);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 单元格里有几段就按段序对齐成几行()
    {
        var body = """
            <w:tbl><w:tr>
              <w:tc><w:p><w:r><w:t>G.W.: 25.5 KGS</w:t></w:r></w:p><w:p><w:r><w:t>N.W.: 22.1 KGS</w:t></w:r></w:p></w:tc>
              <w:tc><w:p><w:r><w:t>CTN: 3 / 12</w:t></w:r></w:p></w:tc>
            </w:tr></w:tbl>
            """;

        // 第二格只有一段：它只出现在第一行，不会被复制两份到第二行
        Assert.Equal(new[] { "G.W.: 25.5 KGS CTN: 3 / 12", "N.W.: 22.1 KGS" }, Read(body));
    }

    [Fact]
    public void 表外段落与表格行按文档序混合()
    {
        var lines = Read(Paragraph("抬头：MACYS") + TwoColumnTable + Paragraph("备注：轻放"));

        // 全角冒号进不了行：Squeeze 先把量纲符号归半角，这一条顺带把那个既有行为钉住
        Assert.Equal(new[] { "抬头:MACYS", "G.W. 25.5 KGS", "N.W. 22.1 KGS", "备注:轻放" }, lines);
    }

    // ---------- 文本框：一份内容只能有一条线 ----------

    /// <summary>Word 存文本框的真实形状：Choice 走 wps，Fallback 走 v:textbox，两份内容一字不差。</summary>
    private const string TextBoxBody = """
        <w:p>
          <w:r>
            <mc:AlternateContent>
              <mc:Choice Requires="wps">
                <w:drawing><wp:anchor><a:graphic>
                  <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
                    <wps:wsp><wps:txbx><w:txbxContent>
                      <w:p><w:r><w:t>MADE IN CHINA</w:t></w:r></w:p>
                    </w:txbxContent></wps:txbx></wps:wsp>
                  </a:graphicData>
                </a:graphic></wp:anchor></w:drawing>
              </mc:Choice>
              <mc:Fallback>
                <w:pict><v:shape><v:textbox><w:txbxContent>
                  <w:p><w:r><w:t>MADE IN CHINA</w:t></w:r></w:p>
                </w:txbxContent></v:textbox></v:shape></w:pict>
              </mc:Fallback>
            </mc:AlternateContent>
          </w:r>
        </w:p>
        """;

    [Fact]
    public void 文本框里的字只算一条线()
    {
        var lines = Read(TextBoxBody);

        Assert.Equal(new[] { "MADE IN CHINA" }, lines);
    }

    [Fact]
    public void 宿主段落自己的文字与文本框各算一条()
    {
        var body = """
            <w:p><w:r><w:t>SIZE:</w:t></w:r>
              <mc:AlternateContent><mc:Choice Requires="wps"><w:drawing><wp:anchor><a:graphic>
                <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
                  <wps:wsp><wps:txbx><w:txbxContent>
                    <w:p><w:r><w:t>60x40x30 CM</w:t></w:r></w:p>
                  </w:txbxContent></wps:txbx></wps:wsp>
                </a:graphicData></a:graphic></wp:anchor></w:drawing></mc:Choice>
              <mc:Fallback><w:pict><v:shape><v:textbox><w:txbxContent>
                <w:p><w:r><w:t>60x40x30 CM</w:t></w:r></w:p>
              </w:txbxContent></v:textbox></v:shape></w:pict></mc:Fallback>
            </mc:AlternateContent></w:p>
            """;

        var lines = Read(body);

        Assert.Equal(new[] { "SIZE:", "60x40x30 CM" }, lines);
    }

    // ---------- 常规正文与坏文件 ----------

    [Fact]
    public void 普通段落照旧一段一行且软回车拆开()
    {
        var body = """
            <w:p><w:r><w:t>PO NO:</w:t><w:tab/><w:t>2024-0817</w:t></w:r></w:p>
            <w:p><w:r><w:t>第一行</w:t><w:br/><w:t>第二行</w:t></w:r></w:p>
            <w:p><w:r><w:t xml:space="preserve">   </w:t></w:r></w:p>
            <w:p><w:r><w:t>ITEM NO: A-778</w:t></w:r></w:p>
            """;

        // 空段不占行；tab 变空格；软回车（w:br）就地拆成两行
        Assert.Equal(new[] { "PO NO: 2024-0817", "第一行 第二行", "ITEM NO: A-778" }, Read(body));
    }

    [Fact]
    public void 缺document_xml的文件说人话而不是抛异常()
    {
        var path = Path.Combine(Path.GetTempPath(), $"labelgou-docx-{Guid.NewGuid():N}.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("word/styles.xml");
        }

        try
        {
            var text = DocxTextReader.ReadFile(path);

            Assert.True(text.IsEmpty);
            Assert.Contains(text.Warnings, m => m.Contains("document.xml", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 老doc改名的文件说人话而不是抛异常()
    {
        var path = Path.Combine(Path.GetTempPath(), $"labelgou-docx-{Guid.NewGuid():N}.docx");
        File.WriteAllBytes(path, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 1, 2, 3 });     // OLE 复合文档头

        try
        {
            var text = DocxTextReader.ReadFile(path);

            Assert.True(text.IsEmpty);
            Assert.Contains(text.Warnings, m => m.Contains("打不开", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
