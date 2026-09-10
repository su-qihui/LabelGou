using System.IO;
using System.Linq;
using LabelGou.Core.Data;
using LabelGou.Core.Recognition;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 「字号/粗细/居中由软件算，不再问模型」这一层的钉子（第 39 棒）。
/// <para>用户 2026-09-10 实测「AI 排版效果差，差在字体大小」。第 31 棒把表格里的字体读出来了，
/// 可读出来之后做的事是<em>把量到的数字压成大白话、再请模型把数字猜回来</em>——量测在软件手里、
/// 结论却要一个概率模型口算回填，所以每轮都不一样。本棒把这三项收回来由软件算。</para>
/// <para>这里钉七件事：① 量到「1 大 3 小同字号」→ 权重比例与内置金沐一致、<strong>三行明细的字号完全相等</strong>、
/// 算出来的数落在房内按真件写死的 41.4pt 上；② 默认格也量得到字号（否则那一行成了盲区，整块证据作废）；
/// ③ 行数对不上 → 一个字不改且留一句人话（不猜闸）；④ 粗体与居中照量到的<strong>盖掉</strong>模型填的；
/// ⑤ 量到的最大字号并列时<strong>一行都不许撑满</strong>（撑满会各缩各的，那正是「字体大小不统一」）；
/// ⑥ 行带几何只有一份（<see cref="RowLayoutSpec.BandHeightsMm"/> 与 <see cref="RowLayoutSpec.Build"/> 同源）；
/// ⑦ 抄标签那块通常<strong>紧贴在列名下面</strong>，量之前要把列名那一格排掉——软件本来就知道列名在第几行，
/// 排掉不是猜；猜的做法（"多出来的一律当列名扔"）会在多出来的其实是脚注时把整块错位，那是悄悄给错字号。</para>
/// </summary>
public sealed class RowFormatEvidenceTests
{
    /// <summary>
    /// 金沐那张的几何：140×100、留白 4、行距 1.5。四行 = 1 大 3 小，
    /// 与 <c>BuiltInTemplates.RowsFour140x100</c>（照真件写死 <c>detailPt: 41.4</c>）同一套。
    /// </summary>
    private static RowLayoutSpec HouseSpec(int rowCount)
    {
        var spec = new RowLayoutSpec
        {
            Name = "测试用",
            WidthMm = 140,
            HeightMm = 100,
            PaddingMm = 4,
            GapMm = 1.5,
        };
        for (var i = 0; i < rowCount; i++)
            spec.Rows.Add(new RowSpec { Content = "第 " + (i + 1) + " 行", Weight = 1, SizePt = 12 });
        return spec;
    }

    /// <summary>
    /// 造一张表：C 列（0 基第 2 列）从第 2 行起连着 <paramref name="rows"/> 行写了字，那就是"抄标签"那一块。
    /// <para>第 1 行是列名（C 列写着"标签样式"），所以那一列其实是「1 格列名 + N 行标签」连着 N+1 格——
    /// 真表就是这样。量标签块时必须传 <c>headerRowIndex: 0</c> 把列名那格排掉，否则读出来多一行、
    /// 跟标签的 N 行对不上（钉子⑦）。</para>
    /// </summary>
    private static string BuildSheet(string name, int rows, bool smallExplicit, bool firstBig = true)
    {
        var grid = new List<string[]> { new[] { "ITEM NO", "件数", "标签样式" } };
        for (var i = 0; i < rows; i++)
            grid.Add(new[] { "olu830-3" + i, "5", "标签第 " + (i + 1) + " 行" });

        var big = firstBig ? new[] { "C2" } : Array.Empty<string>();
        // smallExplicit=false 时明细三行**不设样式**（走工作簿默认那号字体）——真表里这是常态，
        // 而那正是 FormatOf 返回 null、量测口径必须照样报出字号的场合。
        var small = smallExplicit
            ? Enumerable.Range(3, rows - 1).Select(r => "C" + r).ToArray()
            : Array.Empty<string>();

        var bytes = XlsxFixture.Build(grid, bigBoldCells: big, smallCells: small);
        return XlsxFixture.WriteToTempFile(bytes, name);
    }

    // ── ① 权重比例与房内一致、明细三行字号完全相等、绝对值落在真件那个数上 ──

    [Fact]
    public void 量到一大三小_权重与明细字号都跟房内那张金沐对上()
    {
        var path = BuildSheet("ev-house.xlsx", rows: 4, smallExplicit: true);
        var formats = XlsxTableReader.ReadCellFormats(path);
        var spec = HouseSpec(4);
        // 模型填的那几个数（每行都不同的字号）——待会儿要证明它们全被量到的盖掉了。
        spec.Rows[0].SizePt = 30; spec.Rows[1].SizePt = 17; spec.Rows[2].SizePt = 22; spec.Rows[3].SizePt = 9;
        var notes = new List<string>();

        var applied = RowFormatEvidence.TryApply(spec, formats, 2, notes, headerRowIndex: 0);

        Assert.True(applied);
        // 量到的是 14pt 与 10pt → 权重 1.4 : 1 : 1 : 1，与内置模板照真件写死的那一组是同一个数。
        Assert.Equal(1.4, spec.Rows[0].Weight, 3);
        Assert.Equal(1.0, spec.Rows[1].Weight, 3);
        Assert.Equal(1.0, spec.Rows[2].Weight, 3);
        Assert.Equal(1.0, spec.Rows[3].Weight, 3);
        // 大字那一行撑满，明细三行不撑满（撑满会各缩各的，见钉子⑤）。
        Assert.True(spec.Rows[0].Stretch);
        Assert.False(spec.Rows[1].Stretch);
        // **三行明细的字号一模一样**：这是恒等式（相等的量测 → 相等的权重 → 相等的行带 → 相等的字号），
        // 不是启发式。用户圈过的「字体大小不统一」就是这一条杀掉的。
        Assert.Equal(spec.Rows[1].SizePt, spec.Rows[2].SizePt);
        Assert.Equal(spec.Rows[2].SizePt, spec.Rows[3].SizePt);
        // 房内按真件写死的是 41.4pt（量的是字高 14.602mm），软件按行带算出 41.7pt——差 0.7%，同一个数。
        Assert.InRange(spec.Rows[1].SizePt, 41.0, 42.0);
        Assert.Contains(notes, n => n.Contains("量出来算的", StringComparison.Ordinal));
    }

    // ── ② 默认格也量得到字号：FormatOf 对它返回 null（那是给散文去噪），量测口径不能瞎 ──

    [Fact]
    public void 明细行走工作簿默认字体_照样量得到字号而不是盲区()
    {
        var path = BuildSheet("ev-default-font.xlsx", rows: 4, smallExplicit: false);

        var formats = XlsxTableReader.ReadCellFormats(path);
        // 排掉列名那格（第 0 行），剩下的才是"抄标签"那四行。
        var block = formats.Where(f => f.Col == 2 && f.Row != 0).OrderBy(f => f.Row).ToList();

        Assert.Equal(4, block.Count);
        Assert.Equal(14, block[0].SizePt, 3);      // 大字粗体居中那一格
        Assert.True(block[0].Bold);
        Assert.Equal("center", block[0].Align);
        // 三格没设任何样式（fontId 0）：字号是工作簿默认那个 11pt，**不是 0**。
        // 报 0 就等于说"量不到"，那一行的比例算不出来，整块证据只能作废——这是与 FormatOf 的关键区别。
        Assert.All(block.Skip(1), c => Assert.Equal(11, c.SizePt, 3));
        Assert.All(block.Skip(1), c => Assert.False(c.Bold));
        Assert.All(block.Skip(1), c => Assert.Null(c.Align));

        var spec = HouseSpec(4);
        var notes = new List<string>();
        Assert.True(RowFormatEvidence.TryApply(spec, formats, 2, notes, headerRowIndex: 0));
        Assert.Equal(14.0 / 11.0, spec.Rows[0].Weight, 3);
        Assert.Equal(spec.Rows[1].SizePt, spec.Rows[3].SizePt);
    }

    [Fact]
    public void 结构化的那一份不做去噪筛选_散文那一份照旧只报跟主流不一样的()
    {
        // 两个出口口径不同是刻意的：散文报"跟同列主流不一样的那几块"（逐格流水账会把画像淹掉），
        // 而算比例要的是**全部行**，筛过的没法算。同一份文件、同一次读取，两份出口。
        // 这里必须用 smallExplicit: true——明细三行也设上样式，C 列才有"主流"可言；
        // 否则整列只有大字那一格是非默认格式，它自己就成了主流，散文一句都不报。
        var path = BuildSheet("ev-both-exits.xlsx", rows: 4, smallExplicit: true);

        var prose = XlsxTableReader.DescribeCellFormats(path);
        var structured = XlsxTableReader.ReadCellFormats(path);

        Assert.Single(prose, l => l.Contains("C 列"));                    // 散文：一列一句
        Assert.Equal(15, structured.Count);                              // 结构化：每一格一条（5 行 × 3 列），一格都不筛
        Assert.Equal(5, structured.Count(f => f.Col == 2));              // C 列 = 1 格列名 + 4 行标签
    }

    // ── ③ 行数对不上 / 没处去量 → 一个字不改（不猜闸） ──

    [Fact]
    public void 量到的行数跟标签行数对不上_一个字不改只留一句人话()
    {
        var path = BuildSheet("ev-mismatch.xlsx", rows: 4, smallExplicit: true);
        var formats = XlsxTableReader.ReadCellFormats(path);
        var spec = HouseSpec(3);                       // 模型只给了 3 行（比如它漏了一行，或多编了一行品名）
        var notes = new List<string>();

        var applied = RowFormatEvidence.TryApply(spec, formats, 2, notes, headerRowIndex: 0);

        Assert.False(applied);
        // 保持模型填的值：宁可字号是它猜的，也不要软件按一个认错位置的块把字号改错。
        Assert.All(spec.Rows, r => Assert.Equal(12, r.SizePt, 3));
        Assert.All(spec.Rows, r => Assert.Equal(1.0, r.Weight, 3));
        Assert.Contains(notes, n => n.Contains("对不上", StringComparison.Ordinal));
    }

    [Fact]
    public void 同一列里有两处都对得上_认不出哪处是标签样例就不改()
    {
        var path = BuildSheet("ev-two-blocks.xlsx", rows: 4, smallExplicit: true);
        var formats = XlsxTableReader.ReadCellFormats(path).ToList();
        // 再往同一列后面接一段隔着空行的 4 行（0 基 7~10，也就是表里第 8~11 行），凑出第二个候选块。
        for (var r = 7; r < 11; r++) formats.Add(new CellFormat(r, 2, 10, false, null));
        var spec = HouseSpec(4);
        var notes = new List<string>();

        var applied = RowFormatEvidence.TryApply(spec, formats, 2, notes, headerRowIndex: 0);

        Assert.False(applied);
        Assert.All(spec.Rows, r => Assert.Equal(12, r.SizePt, 3));
        Assert.Contains(notes, n => n.Contains("认不出", StringComparison.Ordinal));
    }

    [Fact]
    public void 没说模板抄在哪一列_就不改并且说清为什么()
    {
        var path = BuildSheet("ev-no-column.xlsx", rows: 4, smallExplicit: true);
        var formats = XlsxTableReader.ReadCellFormats(path);
        var spec = HouseSpec(4);
        var notes = new List<string>();

        Assert.False(RowFormatEvidence.TryApply(spec, formats, -1, notes, headerRowIndex: 0));
        Assert.All(spec.Rows, r => Assert.Equal(12, r.SizePt, 3));
        Assert.Contains(notes, n => n.Contains("没处去量", StringComparison.Ordinal));
    }

    /// <summary>
    /// 钉子⑦：<strong>不知道列名在哪一行时，宁可整块不改，也不要"把多出来的那一格当列名扔掉"</strong>。
    /// <para>排掉列名靠的是软件已知的事实（列名在第几行），不是靠"块比标签多一行就多出来的那个是列名"。
    /// 后者在多出来的其实是脚注时会让整块错位——那就成了悄悄给错字号，比不给更坏。</para>
    /// </summary>
    [Fact]
    public void 不知道列名在哪一行_就不许自己把多出来的那格当列名扔()
    {
        var path = BuildSheet("ev-header-unknown.xlsx", rows: 4, smallExplicit: true);
        var formats = XlsxTableReader.ReadCellFormats(path);
        var spec = HouseSpec(4);
        var notes = new List<string>();

        // C 列连着 5 格（1 格列名 + 4 行标签），标签只有 4 行 → 对不上 → 一个字不改。
        var applied = RowFormatEvidence.TryApply(spec, formats, 2, notes, headerRowIndex: -1);

        Assert.False(applied);
        Assert.All(spec.Rows, r => Assert.Equal(12, r.SizePt, 3));
        Assert.All(spec.Rows, r => Assert.Equal(1.0, r.Weight, 3));
        Assert.Contains(notes, n => n.Contains("对不上", StringComparison.Ordinal));

        // 把"列名在第 1 行"这个已知事实递进去，同一张表同一份 spec 就量得出来了。
        var spec2 = HouseSpec(4);
        var notes2 = new List<string>();
        Assert.True(RowFormatEvidence.TryApply(spec2, formats, 2, notes2, headerRowIndex: 0));
        Assert.Equal(1.4, spec2.Rows[0].Weight, 3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 没有量测_保持模型填的值不崩(bool nullInsteadOfEmpty)
    {
        var spec = HouseSpec(4);
        spec.Rows[0].SizePt = 30;
        var notes = new List<string>();

        IReadOnlyList<CellFormat>? formats = nullInsteadOfEmpty ? null : Array.Empty<CellFormat>();
        Assert.False(RowFormatEvidence.TryApply(spec, formats, 2, notes));

        Assert.Equal(30, spec.Rows[0].SizePt, 3);       // CSV 与读不出格式的场合照旧用它填的兜底
        Assert.Empty(notes);                            // 这不值得写一句（每张 CSV 都写就是刷屏）
    }

    [Fact]
    public void 读不了的文件_结构化那一份也返回空不抛()
    {
        var formats = XlsxTableReader.ReadCellFormats(
            Path.Combine(Path.GetTempPath(), "根本没有这个文件-39.xlsx"));

        Assert.Empty(formats);
    }

    // ── ④ 粗体与居中：量到什么就是什么，盖掉模型填的 ──

    [Fact]
    public void 粗体与居中照量到的盖掉模型填的()
    {
        var path = BuildSheet("ev-bold-align.xlsx", rows: 4, smallExplicit: true);
        var formats = XlsxTableReader.ReadCellFormats(path);
        var spec = HouseSpec(4);
        // 模型全填反了：大字行说它不粗、要右对齐；明细行说它们粗、要居中。
        spec.Rows[0].Bold = false; spec.Rows[0].Align = HorizontalAlign.Right;
        for (var i = 1; i < 4; i++) { spec.Rows[i].Bold = true; spec.Rows[i].Align = HorizontalAlign.Center; }
        var notes = new List<string>();

        Assert.True(RowFormatEvidence.TryApply(spec, formats, 2, notes, headerRowIndex: 0));

        Assert.True(spec.Rows[0].Bold);                                  // 量到 14pt 粗体居中
        Assert.Equal(HorizontalAlign.Center, spec.Rows[0].Align);
        for (var i = 1; i < 4; i++)
        {
            Assert.False(spec.Rows[i].Bold);                             // 量到 10pt 常规左对齐
            Assert.Equal(HorizontalAlign.Left, spec.Rows[i].Align);
        }
    }

    [Fact]
    public void 行内容与行序一律不碰_那是模型认出来的活()
    {
        var path = BuildSheet("ev-content-untouched.xlsx", rows: 4, smallExplicit: true);
        var formats = XlsxTableReader.ReadCellFormats(path);
        var spec = HouseSpec(4);
        spec.Rows[2].Content = "QTY：{{col:每箱数量}} pcs";
        var notes = new List<string>();

        Assert.True(RowFormatEvidence.TryApply(spec, formats, 2, notes, headerRowIndex: 0));

        Assert.Equal("第 1 行", spec.Rows[0].Content);
        Assert.Equal("QTY：{{col:每箱数量}} pcs", spec.Rows[2].Content);
        Assert.Equal(4, spec.Rows.Count);
    }

    // ── ⑤ 并列最大 → 一行都不许撑满（撑满会各缩各的，就是「字体大小不统一」） ──

    [Fact]
    public void 两行一样大_就一行都不给撑满()
    {
        // 郑小姐那种两行超大字：表里两行是同一个字号。都撑满的话，各自按内容长度缩字，
        // 值长的那行反而比值短的小——那正是用户圈过的毛病。所以并列时全走显式字号。
        var rows = new List<string[]>
        {
            new[] { "货号", "样式" },
            new[] { "olu-1", "大字一行" },
            new[] { "olu-2", "大字二行" },
        };
        var path = XlsxFixture.WriteToTempFile(
            XlsxFixture.Build(rows, bigBoldCells: new[] { "B2", "B3" }), "ev-tie.xlsx");
        var formats = XlsxTableReader.ReadCellFormats(path);
        var spec = HouseSpec(2);
        var notes = new List<string>();

        Assert.True(RowFormatEvidence.TryApply(spec, formats, 1, notes, headerRowIndex: 0));

        Assert.False(spec.Rows[0].Stretch);
        Assert.False(spec.Rows[1].Stretch);
        Assert.Equal(spec.Rows[0].SizePt, spec.Rows[1].SizePt);          // 一样大就一样大，不许各缩各的
        Assert.Contains(notes, n => n.Contains("几行字一样大", StringComparison.Ordinal));
    }

    // ── ⑥ 行带几何只有一份 ──

    [Fact]
    public void 行带高度与建出来的模板是同一份几何()
    {
        var spec = HouseSpec(4);
        spec.Rows[0].Weight = 1.4;
        spec.Rows[0].Stretch = true;

        var bands = spec.BandHeightsMm();
        var template = spec.Build();

        Assert.NotNull(template);
        Assert.Equal(spec.Rows.Count, bands.Count);
        var texts = template!.Elements.Where(e => e.Kind == ElementKind.Text).ToList();
        for (var i = 0; i < texts.Count; i++)
            Assert.Equal(bands[i], texts[i].Height, 6);                  // 两处各算一遍就一定会长歪
        // 撑满那行的字号 = 行带 × StretchEmPerBand 换成磅，也就是房里那个「2.08 pt/mm」。
        Assert.Equal(RowLayoutSpec.StretchPointForBand(bands[0]), texts[0].FontSizePt, 6);
        Assert.Equal(1.4, bands[0] / bands[1], 3);
    }

    [Fact]
    public void 排不出来时两份口径一致_行带是空的模板是null()
    {
        var spec = HouseSpec(4);
        spec.PaddingMm = 60;                                             // 留白把版面吃光

        Assert.Empty(spec.BandHeightsMm());
        Assert.Null(spec.Build());
    }

    // ── 提示词不再教模型猜字号 ──

    [Fact]
    public void 提示词里不再教模型猜字号_改成明说软件自己会算()
    {
        var prompt = AiSheetProposalPrompt.Build(
            "（画像）", new[] { "一页一枚（纸面跟标签走）" }, 34, 1, "140×100 mm", 0);

        Assert.Contains("你不用管，软件自己会算", prompt);
        Assert.Contains("templateSource", prompt);                       // 那一列就是软件要去量的地方，得写准
        // 第 31 棒那套「读完散文再把数字猜回来」的话一句都不该留着。
        Assert.DoesNotContain("照依据来，不要凭空填", prompt);
        Assert.DoesNotContain("别再自己填 sizePt", prompt);
        Assert.DoesNotContain("明细行的字号别填得比标题行还大", prompt);
    }

    // ── 端到端：提案解析里就地把字号换掉，面板念的与落地的是同一份 ──

    [Fact]
    public void 提案解析时就把字号换成量到的_不是等落地才改()
    {
        var path = BuildSheet("ev-parse.xlsx", rows: 4, smallExplicit: true);
        var formats = XlsxTableReader.ReadCellFormats(path);
        var cols = new[]
        {
            new ColumnPortrait(0, "货号", "货号", null, null, new[] { "olu830-35" }, 4),
            new ColumnPortrait(1, "件数", "件数", null, null, new[] { "5" }, 4),
            new ColumnPortrait(2, "标签样式", "标签样式", null, null, new[] { "BOLAROM" }, 4),
        };
        // headerRow 必须给：提案解析要把「列名在第几行」递给量字号那一步，
        // 缺了它 C 列会被读成「1 格列名 + 4 行标签」= 5 行，跟 4 行标签对不上，整块证据白量。
        // 留白 4 / 行距 1.5 是金沐那张的几何：解析器的默认值是留白 5、行距 2，
        // 那样明细行带算出来是 40.0pt；写成金沐的几何才落在房内按真件量到的 41.4pt 上。
        const string json = """
            {
              "templateSource": "C列",
              "headerRow": 1,
              "paddingmm": 4,
              "gapmm": 1.5,
              "rows": [ { "content": "{{Consignee}}", "sizePt": 30, "bold": false, "align": "right" },
                        { "content": "Item no：{{col:货号}}", "sizePt": 17 },
                        { "content": "QTY：{{col:件数}} pcs", "sizePt": 22 },
                        { "content": "Ctns：{{col:件数}}件", "sizePt": 9 } ]
            }
            """;

        var p = AiSheetProposal.Parse(json, cols, 5, null, formats);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        var rows = p.Layout!.Rows;
        // 模型填的 30/17/22/9 一个都没留下：明细三行现在是同一个数，大字那行撑满。
        Assert.True(rows[0].Stretch);
        Assert.Equal(rows[1].SizePt, rows[2].SizePt);
        Assert.Equal(rows[2].SizePt, rows[3].SizePt);
        Assert.InRange(rows[1].SizePt, 41.0, 42.0);
        Assert.True(rows[0].Bold);                                       // 它说不粗，量到的是粗的
        Assert.Equal(HorizontalAlign.Center, rows[0].Align);             // 它说右对齐，量到的是居中
        Assert.Contains(p.Notes, n => n.Contains("量出来算的", StringComparison.Ordinal));
        // 面板念给人的那几行是拿改完的 spec 生成的，所以不会"说一套落地一套"。
        Assert.NotEmpty(p.Readout.TemplateLines);
    }

    [Fact]
    public void 不递量测时_照旧用模型填的字号()
    {
        var cols = new[] { new ColumnPortrait(0, "货号", "货号", null, null, Array.Empty<string>(), 4) };
        const string json = """
            { "rows": [ { "content": "{{Consignee}}", "sizePt": 30 },
                        { "content": "Item no：{{col:货号}}", "sizePt": 17 } ] }
            """;

        var p = AiSheetProposal.Parse(json, cols, 5);

        Assert.True(p.IsUsable, string.Join("；", p.Errors));
        Assert.Equal(30, p.Layout!.Rows[0].SizePt, 3);
        Assert.Equal(17, p.Layout.Rows[1].SizePt, 3);
    }
}
