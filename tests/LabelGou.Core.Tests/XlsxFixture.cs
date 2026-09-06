using System.IO.Compression;
using System.Text;

namespace LabelGou.Core.Tests;

/// <summary>
/// 在内存里造一个最小可用的 .xlsx，用来测读取器——
/// 不依赖任何二进制测试夹具，测试文件本身就能解释自己在测什么。
/// </summary>
internal static class XlsxFixture
{
    /// <summary>造一个单表 xlsx。<paramref name="rows"/> 第 0 行放特殊指令（见 <see cref="Cell"/>）。</summary>
    public static byte[] Build(
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<string>? sharedStrings = null,
        IReadOnlyList<string>? merges = null,
        IReadOnlyList<string>? dateCells = null,
        IReadOnlyList<string>? decimalCells = null,
        string sheetName = "Sheet1")
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "[Content_Types].xml", ContentTypes);
            Write(archive, "_rels/.rels", RootRels);
            Write(archive, "xl/workbook.xml", Workbook(sheetName));
            Write(archive, "xl/_rels/workbook.xml.rels", WorkbookRels);
            Write(archive, "xl/styles.xml", Styles());
            if (sharedStrings is { Count: > 0 })
                Write(archive, "xl/sharedStrings.xml", SharedStrings(sharedStrings));
            Write(archive, "xl/worksheets/sheet1.xml",
                Sheet(rows, sharedStrings, merges, dateCells, decimalCells));
        }
        return memory.ToArray();
    }

    public static string WriteToTempFile(byte[] bytes, string name = "fixture.xlsx")
    {
        var path = Path.Combine(Path.GetTempPath(), "labelgou-tests", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>单元格写成 "A1=值"；值以 "s:" 前缀表示走共享字符串索引。</summary>
    private static string Sheet(
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<string>? sharedStrings,
        IReadOnlyList<string>? merges,
        IReadOnlyList<string>? dateCells,
        IReadOnlyList<string>? decimalCells)
    {
        var dateSet = new HashSet<string>(dateCells ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var decSet = new HashSet<string>(decimalCells ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<sheetData>");

        for (var r = 0; r < rows.Count; r++)
        {
            sb.Append("<row r=\"").Append(r + 1).Append("\">");
            var cells = rows[r];
            for (var c = 0; c < cells.Count; c++)
            {
                var reference = ColumnLetter(c) + (r + 1);
                var value = cells[c];
                var styleAttr = dateSet.Contains(reference) ? " s=\"1\"" : decSet.Contains(reference) ? " s=\"2\"" : string.Empty;

                if (value.StartsWith("s:", StringComparison.Ordinal) && sharedStrings is not null)
                {
                    var index = int.Parse(value[2..]);
                    sb.Append("<c r=\"").Append(reference).Append("\" t=\"s\"").Append(styleAttr)
                      .Append("><v>").Append(index).Append("</v></c>");
                }
                else if (value.StartsWith("n:", StringComparison.Ordinal))
                {
                    sb.Append("<c r=\"").Append(reference).Append("\"").Append(styleAttr)
                      .Append("><v>").Append(value[2..]).Append("</v></c>");
                }
                else if (value.StartsWith("inline:", StringComparison.Ordinal))
                {
                    sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"").Append(styleAttr)
                      .Append("><is><t>").Append(Escape(value[7..])).Append("</t></is></c>");
                }
                else
                {
                    sb.Append("<c r=\"").Append(reference).Append("\" t=\"str\"").Append(styleAttr)
                      .Append("><v>").Append(Escape(value)).Append("</v></c>");
                }
            }
            sb.Append("</row>");
        }

        sb.Append("</sheetData>");
        if (merges is { Count: > 0 })
        {
            sb.Append("<mergeCells count=\"").Append(merges.Count).Append("\">");
            foreach (var m in merges) sb.Append("<mergeCell ref=\"").Append(m).Append("\"/>");
            sb.Append("</mergeCells>");
        }
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static string SharedStrings(IReadOnlyList<string> items)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"")
          .Append(items.Count).Append("\" uniqueCount=\"").Append(items.Count).Append("\">");
        foreach (var item in items)
        {
            if (item.StartsWith("rich:", StringComparison.Ordinal))
            {
                // 富文本：<si><r><t>…</t></r><r><t>…</t></r></si>，读取器应拼成完整字符串
                var parts = item[5..].Split('|');
                sb.Append("<si>");
                foreach (var part in parts) sb.Append("<r><t>").Append(Escape(part)).Append("</t></r>");
                sb.Append("</si>");
            }
            else
            {
                sb.Append("<si><t>").Append(Escape(item)).Append("</t></si>");
            }
        }
        sb.Append("</sst>");
        return sb.ToString();
    }

    /// <summary>cellXfs: 0=通用, 1=内置日期格式(numFmtId 14), 2=自定义 0.00（两位小数）。</summary>
    private static string Styles() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <numFmts count="1"><numFmt numFmtId="164" formatCode="0.00"/></numFmts>
          <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
          <cellXfs count="3">
            <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
            <xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
            <xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
          </cellXfs>
        </styleSheet>
        """;

    private static string Workbook(string sheetName) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"" + Escape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

    private static string WorkbookRels =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>" +
        "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    private static string RootRels =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private static string ContentTypes =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "</Types>";

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var stream = entry.Open();
        var bytes = new UTF8Encoding(false).GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string ColumnLetter(int zeroBased)
    {
        var n = zeroBased + 1;
        var sb = new StringBuilder();
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            n = (n - 1) / 26;
        }
        return sb.ToString();
    }
}
