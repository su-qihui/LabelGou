using System.Text.Json;
using LabelGou.Core.Interop.Cdr;
using LabelGou.Core.Templates;
using Xunit;
using Xunit.Abstractions;

namespace LabelGou.Core.Tests;

/// <summary>
/// B 通道（不启动 CorelDRAW，离线直解 <c>.cdr</c> 二进制）的硬判据。
/// <para>
/// 对账基准是 A 通道（COM 驱动本机 CorelDRAW X4 逐对象导出）留下的 <c>*.cdrx.json</c> 夹具，
/// 真样件原件只读。锚点数字全部由 <c>labegou-CDR\other\_probe\cdr3-offline\</c> 那套 Python 探针
/// 实测并经主会话独立复算，跑红的顺序是"先怀疑移植，别去改锚点"。
/// </para>
/// <para>
/// 夹具定位按结构特征走（<see cref="FixtureStore"/>）：从测试程序集往上退到"同时放着主仓
/// （有 <c>LabelGou.sln</c>）与真样件目录"的那一级，再按扩展名认 <c>.cdr</c> / <c>.cdrx.json</c>。
/// <b>不认目录名、不写死盘符、找不到就明确抛</b>——分区的 <c>TestPaths</c> 把目录名写死导致换机全红，
/// 而"0 条测试跑过"当"测过了"更糟，所以这里一条都不静默跳过。
/// </para>
/// <para>
/// <b>判据要能红——两条已真跑过变异（跑完即还原，并按 sha256 核过与改前逐字节一致）：</b>
/// <list type="bullet">
/// <item>M1 <c>+28 → +24</c>（cmpr 体的 zlib 起点错位）：实测 <b>52 红 / 7 绿</b>，只剩 vrsn 那 6 条与"读不了必须抛"那条；
/// 容器层一错位，L1/L2/盒/文字/字号/OLE 全塌。</item>
/// <item>M2 页心框法 → 绝对原点：实测 <b>8 红 / 51 绿</b>，红的正好是
/// <see cref="Every_object_box_matches_A_within_half_a_micron"/>（6 份 theory）、
/// <see cref="Group_box_also_matches_A_so_the_hierarchy_is_not_invented"/>、
/// <see cref="Page_center_frame_is_the_only_one_that_reaches_A_and_absolute_origin_is_off_by_half_a_page"/>，
/// 容器/块树/文字/字号/OLE 一条不动——说明盒的判据钉在框法上，不是钉在"整体能跑"上。</item>
/// <item>未跑但同构的三条：把长度池下标改回 1 字节 →
/// <see cref="Pool_index_is_u32_records_equal_pool_items_zero_residual"/> 与
/// <see cref="Six_samples_total_36_object_containers_and_page_sizes_match_A"/> 必红（金沐按 1 字节在 15138/19134 越界、条码净负 28361 B）；
/// 把 <c>char_description</c> 的 bit0 分帧换成"看首字节 ≥0x80" →
/// <see cref="Chinese_texts_survive_the_two_byte_framing"/> 与 <see cref="Text_is_verbatim_equal_to_A"/> 必红
/// （邱总「水」= 34 6C，前字节是可打印的 '4'）；把 OLE 判成隐形 →
/// <see cref="Ole_objects_stay_visible_kind_unknown_and_name_olel"/> 必红；把 vrsn 的长度当值 →
/// <see cref="Vrsn_len_is_two_and_value_is_1400"/> 必红。</item>
/// </list>
/// </para>
/// </summary>
public class CdrBinaryChannelTests
{
    const double ToleranceMm = 0.0005;

    /// <summary>探针 p4/p5 实测的六份 <c>LIST[obj ]</c> 对象容器数（＝A 的非群组对象数），合计 36。</summary>
    static readonly (string BaseName, int ObjContainers)[] Samples =
    {
        ("7.8金沐唛头", 2),
        ("OLU优化", 8),
        ("邱总", 2),
        ("TOP优化", 6),
        ("广州郑小姐唛头流水26.7.7", 1),
        ("1234567891231", 17),
    };

    static readonly string[] BaseNames = Array.ConvertAll(Samples, s => s.BaseName);

    readonly ITestOutputHelper _out;

    public CdrBinaryChannelTests(ITestOutputHelper outp) => _out = outp;

    // ---------------------------------------------------------------- 夹具定位

    /// <summary>一份真样件：原件 <c>.cdr</c> 与 A 通道产物 <c>.cdrx.json</c>。</summary>
    sealed record SamplePair(string BaseName, string CdrPath, string CdrxPath);

    static class FixtureStore
    {
        static readonly Lazy<Dictionary<string, SamplePair>> Cache = new(Build);

        public static SamplePair Find(string baseName)
        {
            var map = Cache.Value;
            if (!map.TryGetValue(baseName, out var pair))
                throw new FileNotFoundException(
                    $"找不到真样件「{baseName}」：已按结构特征在 {WorkspaceRoots()} 里找过 *.cdr 与 *.cdrx.json。" +
                    $"B 通道的判据必须以真样件为基准，缺件就是没测——不许跳过。");
            return pair;
        }

        public static IReadOnlyList<string> AllKeys => Cache.Value.Keys.ToList();

        static string WorkspaceRoots()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LabelGou.sln"))) dir = dir.Parent;
            return dir?.Parent?.FullName ?? "(没找到含 LabelGou.sln 的祖先目录)";
        }

        static Dictionary<string, SamplePair> Build()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LabelGou.sln"))) dir = dir.Parent;
            if (dir is null)
                throw new FileNotFoundException(
                    $"从 {AppContext.BaseDirectory} 往上退，没有祖先目录里有 LabelGou.sln——" +
                    $"这条测试要求真样件在仓库旁边（labelgou-CL 那种），定位方式不认目录名，只认\"能同时看到主仓与样件\"这一结构特征。");
            var workspace = dir.Parent
                            ?? throw new FileNotFoundException($"主仓在盘根 {dir.FullName}，旁边没有放样件的那一级");

            var cdrs = Scan(workspace, ".cdr");
            var cdrxs = Scan(workspace, ".cdrx.json");
            var map = new Dictionary<string, SamplePair>(StringComparer.Ordinal);
            foreach (var (baseName, cdrPath) in cdrs)
                if (cdrxs.TryGetValue(baseName, out var cdrxPath)) map[baseName] = new SamplePair(baseName, cdrPath, cdrxPath);
            return map;
        }

        /// <summary>按扩展名在 workspace 下（限深、跳过 bin/obj/隐藏目录）收文件，key 是不含扩展名的文件名。</summary>
        static Dictionary<string, string> Scan(DirectoryInfo root, string extension)
        {
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            void Recurse(DirectoryInfo d, int depth)
            {
                if (depth > 4) return;
                foreach (var f in d.EnumerateFiles())
                {
                    if (!f.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                    if (extension == ".cdr" && !f.Name.EndsWith(".cdr", StringComparison.OrdinalIgnoreCase)) continue;
                    var key = extension == ".cdr"
                        ? Path.GetFileNameWithoutExtension(f.Name)
                        : Path.GetFileName(f.Name)[..^".cdrx.json".Length];
                    found.TryAdd(key, f.FullName);
                }
                foreach (var sub in d.EnumerateDirectories())
                {
                    if (sub.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (sub.Name is "bin" or "obj" or "node_modules" or ".git" or "TestResults") continue;
                    Recurse(sub, depth + 1);
                }
            }
            Recurse(root, 0);
            return found;
        }
    }

    static CdrBinaryDocument Parse(string baseName)
    {
        var p = FixtureStore.Find(baseName);
        var doc = CdrBinaryParser.ParseFileDetailed(p.CdrPath);
        return doc;
    }

    static CdrxDoc ReadChannelA(string baseName) => CdrxReader.ReadFile(FixtureStore.Find(baseName).CdrxPath);

    /// <summary>A 的非群组对象（树前序），与 B 的 <c>Flattened()</c> 同向配对。</summary>
    static List<CdrxObject> AObjects(CdrxDoc a) => a.Flattened().Where(o => o.Kind != CdrxKinds.Group).ToList();

    static List<CdrxObject> BObjects(CdrxDoc b) => b.Flattened().Where(o => o.Kind != CdrxKinds.Group).ToList();

    // ---------------------------------------------------------------- 判据 0：容器层三条已坐实事实

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Vrsn_len_is_two_and_value_is_1400(string baseName)
    {
        var d = Parse(baseName);
        // 「vrsn 的长度是 2、值是 1400」：把长度当值会读成 2
        Assert.Equal(2, d.VersionFieldLength);
        Assert.Equal(1400, d.Version);
        Assert.Equal("1400", d.Doc.Source.CorelVersion);
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void CmprHead_zlib_at_plus_28_and_two_streams_match_declaration(string baseName)
    {
        var d = Parse(baseName);
        Assert.Equal(2, d.Container.Segments.Count);      // 实测恒 2 段
        foreach (var s in d.Segments)
        {
            Assert.Equal("CPng", s.CpngTag);
            Assert.Equal("01000400", s.MagicHex);
            Assert.Contains("相符", s.Part1Note);
            Assert.Contains("相符", s.Part2Note);
            Assert.Matches(@"实解 \d+ B / 声明 \d+ B 相符", s.Part1Note);
        }
        // 探针实测的块流长度（打回 +24/+32 这两句必红：起点错位 → inflate 直接失败）
        var tree = d.Segments.Single(s => s.ObjectContainers > 0);
        switch (baseName)
        {
            case "7.8金沐唛头": Assert.Equal(19134, tree.Part1Length); break;
            case "1234567891231": Assert.Equal(59242, tree.Part1Length); break;
            case "邱总": Assert.Equal(19310, tree.Part1Length); break;
            case "TOP优化": Assert.Equal(31802, tree.Part1Length); break;
            case "OLU优化": Assert.Equal(25358, tree.Part1Length); break;
            case "广州郑小姐唛头流水26.7.7": Assert.Equal(16944, tree.Part1Length); break;
        }
    }

    // ---------------------------------------------------------------- 判据 0b：池下标是 u32、零残余

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Pool_index_is_u32_records_equal_pool_items_zero_residual(string baseName)
    {
        var d = Parse(baseName);
        foreach (var s in d.Segments)
        {
            Assert.True(s.RecordsEqualPoolItems,
                $"{baseName} cmpr#{s.Index}：块记录数 {s.Records} ≠ 长度池项数 {s.PoolItems}（池下标按 u32 才恒等）");
            Assert.Equal(0, s.Residuals);
            Assert.Equal(0, s.PoolTailBytes);
            Assert.True(s.Part2Length % 4 == 0, $"{baseName} cmpr#{s.Index}：长度池 {s.Part2Length} B 不是 4 的整数倍");
        }
        // 探针 p2/p5 实测的池项数（金沐按 1 字节下标在 15138/19134 处越界，条码净负 28361 B）
        var expected = baseName switch
        {
            "7.8金沐唛头" => 101,
            "OLU优化" => 166,
            "邱总" => 99,
            "TOP优化" => 148,
            "广州郑小姐唛头流水26.7.7" => 83,
            "1234567891231" => 274,
            _ => throw new ArgumentOutOfRangeException(nameof(baseName)),
        };
        Assert.Equal(expected, d.Segments.Max(s => s.PoolItems));
    }

    public static TheoryData<string> SampleNames()
    {
        var t = new TheoryData<string>();
        foreach (var n in BaseNames) t.Add(n);
        return t;
    }

    // ---------------------------------------------------------------- 判据 1：对象容器数 = A 的非群组对象数

    [Theory]
    [MemberData(nameof(SampleNamesAndCounts))]
    public void ObjContainerCount_equals_A_non_group_count(string baseName, int expected)
    {
        var d = Parse(baseName);
        var a = ReadChannelA(baseName);
        Assert.Equal(expected, d.ObjectContainerCount);                       // 文件里的 LIST[obj ] 数
        Assert.Equal(AObjects(a).Count, d.Facts.Count(f => !f.IsGroup));      // 与 A 的非群组对象数同数
        Assert.True(d.GroupContainerCount <= 1, $"{baseName}：群组容器数 {d.GroupContainerCount} 与实测（只有 OLU 1 只）不符");
    }

    public static TheoryData<string, int> SampleNamesAndCounts()
    {
        var t = new TheoryData<string, int>();
        foreach (var (n, c) in Samples) t.Add(n, c);
        return t;
    }

    [Fact]
    public void Six_samples_total_36_object_containers_and_page_sizes_match_A()
    {
        var total = 0;
        foreach (var (baseName, expected) in Samples)
        {
            var d = Parse(baseName);
            var a = ReadChannelA(baseName);
            total += d.ObjectContainerCount;
            Assert.Equal(expected, d.ObjectContainerCount);
            // 页尺寸是 F3 框法的输入，必须与 A 报的页一致（金沐/邱总/TOP 140×100，OLU/郑小姐 160×120，条码 210×297）
            Assert.Equal(a.Page.W!.Value, d.PageWidthMm!.Value, 4);
            Assert.Equal(a.Page.H!.Value, d.PageHeightMm!.Value, 4);
        }
        Assert.Equal(36, total);
    }

    // ---------------------------------------------------------------- 判据 2：逐对象盒与 A 对账

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Every_object_box_matches_A_within_half_a_micron(string baseName)
    {
        var d = Parse(baseName);
        var a = ReadChannelA(baseName);
        var aObjs = AObjects(a);
        var bObjs = BObjects(d.Doc);
        Assert.Equal(aObjs.Count, bObjs.Count);
        var worst = 0.0;
        for (var i = 0; i < aObjs.Count; i++)
        {
            var ab = aObjs[i].Box;
            var bb = bObjs[i].Box;
            Assert.NotNull(ab);
            Assert.NotNull(bb);
            foreach (var (k, av, bv) in new (string, double, double)[]
                     {
                         ("x", ab.X, bb.X), ("y", ab.Y, bb.Y), ("w", ab.W, bb.W), ("h", ab.H, bb.H),
                     })
            {
                var delta = Math.Abs(av - bv);
                worst = Math.Max(worst, delta);
                Assert.True(delta <= ToleranceMm,
                    $"{baseName} obj{i} 的 {k} 差 {delta:0.00000} mm（上限 {ToleranceMm}）：A={av:0.0000} B={bv:0.0000}");
            }
        }
        _out.WriteLine($"{baseName}: {aObjs.Count} 只配对，最大 |Δ| = {worst:0.000000} mm");
        Assert.True(worst <= ToleranceMm);
    }

    [Fact]
    public void Group_box_also_matches_A_so_the_hierarchy_is_not_invented()
    {
        // 六份里只有 OLU 有群组：A 报的群组盒也必须是页中心框法折出来的同一个盒
        var d = Parse("OLU优化");
        var a = ReadChannelA("OLU优化");
        var ag = a.Flattened().First(o => o.Kind == CdrxKinds.Group);
        var bg = d.Doc.Flattened().First(o => o.Kind == CdrxKinds.Group);
        Assert.NotNull(ag.Box);
        Assert.NotNull(bg.Box);
        Assert.InRange(bg.Box!.X - ag.Box!.X, -ToleranceMm, ToleranceMm);
        Assert.InRange(bg.Box.Y - ag.Box.Y, -ToleranceMm, ToleranceMm);
        Assert.InRange(bg.Box.W - ag.Box.W, -ToleranceMm, ToleranceMm);
        Assert.InRange(bg.Box.H - ag.Box.H, -ToleranceMm, ToleranceMm);
        Assert.Equal(ag.Children!.Count, bg.Children!.Count);
    }

    // ---------------------------------------------------------------- 判据 3：文字 19/19 逐字等

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Text_is_verbatim_equal_to_A(string baseName)
    {
        var d = Parse(baseName);
        var a = ReadChannelA(baseName);
        var aObjs = AObjects(a);
        var bObjs = BObjects(d.Doc);
        var equal = 0;
        var withText = 0;
        for (var i = 0; i < aObjs.Count; i++)
        {
            var at = aObjs[i].Text?.Contents;
            var bt = bObjs[i].Text?.Contents;
            if (at is null && bt is null) continue;
            withText++;
            Assert.True(at == bt, $"{baseName} obj{i} 文字不等：\n  A={Json(at)}\n  B={Json(bt)}");
            equal++;
        }
        var expected = baseName switch
        {
            "7.8金沐唛头" => 2,
            "OLU优化" => 2,
            "邱总" => 2,
            "TOP优化" => 3,
            "广州郑小姐唛头流水26.7.7" => 1,
            "1234567891231" => 9,
            _ => 0,
        };
        Assert.Equal(expected, withText);
        Assert.Equal(expected, equal);
    }

    [Fact]
    public void Six_samples_total_19_verbatim_equal_texts()
    {
        var total = 0;
        foreach (var (baseName, _) in Samples)
        {
            var d = Parse(baseName);
            var a = ReadChannelA(baseName);
            var aObjs = AObjects(a);
            var bObjs = BObjects(d.Doc);
            for (var i = 0; i < aObjs.Count && i < bObjs.Count; i++)
                if (aObjs[i].Text?.Contents is not null || bObjs[i].Text?.Contents is not null)
                {
                    Assert.Equal(aObjs[i].Text?.Contents, bObjs[i].Text?.Contents);
                    total++;
                }
        }
        Assert.Equal(19, total);
    }

    [Fact]
    public void Jinmu_object_zero_three_lines_exactly_with_fullwidth_colon_and_cr_normalized()
    {
        var d = Parse("7.8金沐唛头");
        var t = BObjects(d.Doc)[0].Text!;
        Assert.Equal("Item no：olu830-35\nQTY：144 pcs\nCtns：5件", t.Contents);
        // 全角冒号 U+FF1A 与裸 \r：归一在 CdrxText.Contents 的 setter 里做，这里验它没被绕开
        Assert.Equal(3, t.Lines.Length);
        Assert.Equal("Item no：olu830-35", t.Lines[0]);
        Assert.Equal("QTY：144 pcs", t.Lines[1]);
        Assert.Equal("Ctns：5件", t.Lines[2]);
        Assert.Contains('：', t.Contents);
        Assert.DoesNotContain('\r', t.Contents);
        Assert.DoesNotContain("\r\n", t.Contents);
        // 金沐 obj0 的 text_data 原始字节（探针夹具 anchors.json 的 paragraph0.text_hex）
        var para = d.Facts[0].Txsm!.Paragraphs[0];
        Assert.Equal(37, para.NumChars);
        Assert.Equal(41, para.NumBytesInText);
        Assert.Equal("49 74 65 6d 20 6e 6f 1a ff 6f 6c 75 38 33 30 2d 33 35 0d 51 54 59 1a ff 31 34 34 20 70 63 73 0d 43 74 6e 73 1a ff 35 f6 4e",
            para.TextHex);
        Assert.Equal("char_description.flags.bit0", para.Framing);
        Assert.True(d.Facts[0].Txsm!.ConsumedExact);
        Assert.Equal(0, d.Facts[0].Txsm!.Residual);
    }

    [Fact]
    public void Chinese_texts_survive_the_two_byte_framing()
    {
        // 邱总「香水」与 TOP「件数CTN：3 件」：「水」= 34 6C，前字节是可打印 '4'，任何"看首字节"的规则都会切错
        var qiu = BObjects(Parse("邱总").Doc)[0].Text!.Contents;
        Assert.Contains("香水 perfume", qiu, StringComparison.Ordinal);
        Assert.Contains("ITEM No：aym6101", qiu, StringComparison.Ordinal);
        Assert.Contains("QTY：96 PCS", qiu, StringComparison.Ordinal);

        var top = BObjects(Parse("TOP优化").Doc)[1].Text!.Contents;
        Assert.Contains("件数CTN：3 件", top, StringComparison.Ordinal);
        Assert.Contains("装箱数QTY：48 PCS", top, StringComparison.Ordinal);
        Assert.Contains("MADE IN CHINA", top, StringComparison.Ordinal);

        var barcode = BObjects(Parse("1234567891231").Doc).Select(o => o.Text?.Contents).ToList();
        Assert.Contains("1234567891231", barcode);
        Assert.Contains("EAN-13", barcode);
        Assert.Contains("ITF-14", barcode);
        Assert.Contains("CodaBar", barcode);

        var zheng = BObjects(Parse("广州郑小姐唛头流水26.7.7").Doc)[0].Text!.Contents;
        Assert.Equal("QI YUE:\n AJ7-QI YUE: Aj9", zheng);
    }

    // ---------------------------------------------------------------- 判据 4：字号

    [Fact]
    public void Jinmu_object_zero_font_size_is_the_measured_41_3915_pt()
    {
        var d = Parse("7.8金沐唛头");
        var a = ReadChannelA("7.8金沐唛头");
        var aPt = AObjects(a)[0].Text!.Font!.SizePt!.Value;     // A(COM) 报 41.39099884033203

        // ① 探针口径：把 A 的 pt 折回 Corel 定点，在 stlt 体里按 ±3 找——找到了才说明字号真在文件里
        var wantRaw = (long)Math.Round(aPt * 25.4 / 72.0 * 10000.0);
        Assert.Equal(146018, wantRaw);
        Assert.True(d.FindStltSizeRaw(wantRaw) >= 0, "stlt 体里没找到 A 报的那个字号 raw（±3）");

        // ② stride 60 的样式记录扫出来的一条，pt 值必须就是实测的 41.3915
        var rec = d.StltStyleRecords.FirstOrDefault(r => r.SizeRaw == 146020);
        Assert.NotNull(rec);
        Assert.Equal(41.3915, rec!.SizePt, 4);                            // 记录里的 SizePt 是 4 位小数
        var exactPt = CdrBinaryUnits.Pt(rec.SizeRaw);                     // 没舍入的那个数才拿来对 Δ
        Assert.InRange(Math.Abs(exactPt - aPt), 0, ToleranceMm);          // 0.00049722 ≤ 0.0005
        Assert.Equal(14.6020, rec.SizeMm, 4);

        // ③ 链串到了对象上：字号要真填进 Text.Font，且与 A(COM) 的实测值同量级（Δ≤0.0005 pt）
        var b0 = BObjects(d.Doc)[0].Text!.Font!;
        Assert.NotNull(b0.SizePt);
        Assert.InRange(Math.Abs(b0.SizePt!.Value - aPt), 0, ToleranceMm);
        Assert.Equal(41.3915, b0.SizePt!.Value, 4);
        // 名字比 A 还多：A 侧 Shape.Font.Name 对这块是空串（COM 没报），B 从 font 块表查到了真名
        Assert.True(string.IsNullOrEmpty(AObjects(a)[0].Text!.Font!.Name), "A 侧这里若有名字就要两边对得上");
        Assert.Equal("Adobe Gothic Std B", b0.Name);

        // ④ 凭据行不许在解出之后被删掉：它自报"串上几只"，那就必须和数据里的数一致
        var chain = Assert.Single(d.Doc.Source.Degraded,
            s => s.Contains("字号链", StringComparison.Ordinal) && s.Contains("stlt（at=", StringComparison.Ordinal));
        Assert.Contains("这条链是 ID→ID 按值查，不是由盒高反推", chain, StringComparison.Ordinal);
        var claimed = int.Parse(chain.Split("链上串出字号 ")[1].Split(' ')[0]);
        Assert.Equal(claimed, BObjects(d.Doc).Count(o => o.Text?.Font?.SizePt is not null));
        Assert.Contains(BObjects(d.Doc)[0].Notes!, s => s.Contains("字号链：txsm 段落 style_id", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Every_size_A_reports_B_reproduces(string baseName)
    {
        // 用户那句"大小不同"的总闸：A 报得出字号的文字对象，B 必须报得出一样的（不是按盒高猜的）。
        var a = ReadChannelA(baseName);
        var d = Parse(baseName);
        var aObjs = AObjects(a);
        var bObjs = BObjects(d.Doc);
        Assert.Equal(aObjs.Count, bObjs.Count);

        var checked_ = 0;
        for (var i = 0; i < aObjs.Count; i++)
        {
            if (aObjs[i].Text?.Font?.SizePt is not { } aPt) continue;
            checked_++;
            var bPt = bObjs[i].Text?.Font?.SizePt;
            Assert.NotNull(bPt);
            Assert.InRange(Math.Abs(bPt!.Value - aPt), -ToleranceMm, ToleranceMm);
            if (aObjs[i].Text!.Font!.Name is { Length: > 0 } aName)   // A 报空串＝COM 那侧没给，不能要求 B 也空
                Assert.Equal(aName, bObjs[i].Text!.Font!.Name);
        }
        Assert.True(checked_ > 0, $"{baseName}：A 一个字号都没报，这条判据等于没测");
    }

    [Fact]
    public void Trfd_stretch_is_carried_and_is_the_number_that_makes_the_measured_box_fit()
    {
        // 用户圈的"拉伸比例不同"。金沐两块的横向拉伸有两个独立来源撞到第 6 位小数：
        // B 从二进制 trfd 解出 0.821798 / 0.919044，C 通道（CDR 自己导的 SVG）matrix 的 a 项是同一对数
        // （other\_probe\cdr2-offline\diff-table.md §变换）。所以这两个数不是抄来的巧合。
        var b = BObjects(Parse("7.8金沐唛头").Doc);
        Assert.Equal(0.821798, b[0].ScaleX!.Value, 6);
        Assert.Equal(0.919044, b[1].ScaleX!.Value, 6);
        Assert.Null(b[0].ScaleY);     // 1.0＝没拉伸＝缺字段，不许写个 1 假装读到了
        Assert.Null(b[1].ScaleY);

        // 另外三份的横向压缩。凭什么说它们是真的：按字宽粗估的墨迹乘上这个数才落进 Corel 报的那个盒里
        // （邱总 155.7→106.7 对盒 111.0；郑小姐 337.9→158.6 对盒 141.4；金沐 135.2→111.1 对盒 116.3），
        // 不乘就有整行伸出纸外的——那正是用户圈的"第二行超出范围"。
        Assert.Equal(0.685218, BObjects(Parse("邱总").Doc)[0].ScaleX!.Value, 6);
        Assert.Equal(0.469221, BObjects(Parse("广州郑小姐唛头流水26.7.7").Doc)[0].ScaleX!.Value, 6);
        Assert.Equal(0.666059, BObjects(Parse("OLU优化").Doc).Last(o => o.Text is not null).ScaleX!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void No_stretch_value_leaves_the_window_the_template_can_express(string baseName)
    {
        // 群组自己的缩放会算到成员头上（OLU 组内那只矩形报的是 2.55×1.35）：那是"这一层被摆成多大"，
        // 不是"用户拉了它"。带出来可以，但落进模板前必须还在软件能表达的区间里，越界的只许点名不许照抄。
        var doc = Parse(baseName).Doc;
        Assert.All(doc.Flattened(), o => Assert.True(
            o.ScaleX is not double x || (x >= TemplateValidator.MinStretch && x <= TemplateValidator.MaxStretch),
            $"{baseName}：横向拉伸 {o.ScaleX} 落在软件能表达的区间外"));
        Assert.All(doc.Flattened(), o => Assert.True(
            o.ScaleY is not double y || (y >= TemplateValidator.MinStretch && y <= TemplateValidator.MaxStretch),
            $"{baseName}：纵向拉伸 {o.ScaleY} 落在软件能表达的区间外"));
    }

    // ---------------------------------------------------------------- 判据 5：F3 页中心框法（含反证）

    [Fact]
    public void Page_center_frame_is_the_only_one_that_reaches_A_and_absolute_origin_is_off_by_half_a_page()
    {
        var d = Parse("7.8金沐唛头");
        var a = ReadChannelA("7.8金沐唛头");
        var raw = d.Facts[0].BboxRaw!;
        Assert.Equal(new[] { -596204, 99192, 566631, -394501 }, raw);   // 探针夹具 anchors.json 的原始四元组

        var (pw, ph) = (d.PageWidthMm!.Value, d.PageHeightMm!.Value);    // 140×100
        var f3 = CdrBinaryCodecs.PageCenterBox(raw, pw, ph);
        Assert.Equal(10.3796, f3.LeftTopXmm, 4);
        // Y 向上口径（页心起算）：max(y0,y1)=9.9192 → 折到页底原点就是 50+9.9192=59.9192
        Assert.Equal(59.9192, ph / 2.0 + Math.Max(CdrBinaryUnits.Mm(raw[1]), CdrBinaryUnits.Mm(raw[3])), 4);
        Assert.Equal(40.0808, f3.TopYmm, 4);
        Assert.Equal(116.2835, f3.WidthMm, 4);
        Assert.Equal(49.3693, f3.HeightMm, 4);

        var abox = AObjects(a)[0].Box!;
        Assert.InRange(f3.LeftTopXmm - abox.X, -ToleranceMm, ToleranceMm);
        Assert.InRange(f3.TopYmm - abox.Y, -ToleranceMm, ToleranceMm);

        // 反证：把文件坐标当"以页角为原点"（错算法）会差整整半个页面，且差值是常数
        var f1 = CdrBinaryCodecs.AbsoluteOriginBox(raw, pw, ph);
        Assert.Equal(-pw / 2.0, Math.Round(f1.LeftTopXmm - f3.LeftTopXmm, 6));      // −70
        Assert.Equal(+ph / 2.0, Math.Round(f1.TopYmm - f3.TopYmm, 6));              // +50
        Assert.Equal(-70.0, Math.Round(f1.LeftTopXmm - abox.X, 4));
        Assert.Equal(+50.0, Math.Round(f1.TopYmm - abox.Y, 4));
        Assert.Equal(0.0, Math.Round(f1.WidthMm - f3.WidthMm, 6));
        Assert.Equal(0.0, Math.Round(f1.HeightMm - f3.HeightMm, 6));
        _out.WriteLine($"F3=({f3.LeftTopXmm:0.0000},{f3.TopYmm:0.0000}) F1=({f1.LeftTopXmm:0.0000},{f1.TopYmm:0.0000}) A=({abox.X:0.0000},{abox.Y:0.0000})");
    }

    // ---------------------------------------------------------------- 判据 6：OLE 只登记、不判隐形

    [Theory]
    [InlineData("1234567891231", 8)]
    [InlineData("TOP优化", 1)]
    public void Ole_objects_stay_visible_kind_unknown_and_name_olel(string baseName, int expected)
    {
        var d = Parse(baseName);
        var ole = d.Facts.Where(f => f.HasOlel).ToList();
        Assert.Equal(expected, ole.Count);
        foreach (var f in ole)
        {
            var o = f.Object;
            Assert.NotEqual(CdrxKinds.Group, o.Kind);
            Assert.Equal(CdrxKinds.Unknown, o.Kind);                     // 认不得的类型号不许硬套语义
            Assert.True(o.IsVisible, $"{baseName}：OLE 对象被判隐形了（读不出来≠不存在）");
            Assert.True(o.Visible is not false, "OLE 对象的 Visible 被写成了 false");
            Assert.NotNull(o.Box);
            Assert.NotNull(o.Notes);
            Assert.Contains(o.Notes!, s => s.Contains("olel", StringComparison.Ordinal));
            Assert.Contains(o.Notes!, s => s.Contains("0x0A", StringComparison.Ordinal) && s.Contains("10", StringComparison.Ordinal));
            Assert.Contains(o.Notes!, s => s.Contains("不判隐形", StringComparison.Ordinal));
        }
        Assert.All(d.Doc.Flattened().Where(o => o.Kind == CdrxKinds.Unknown), o => Assert.True(o.IsVisible));
    }

    // ---------------------------------------------------------------- 判据 7：降级不许静默

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Everything_unsolved_is_named_not_silently_missing(string baseName)
    {
        var d = Parse(baseName);
        var deg = string.Join("\n", d.Doc.Source.Degraded);
        foreach (var must in new[] { "fild", "outl", "字号链", "查到名字", "节点级几何", "图层", "olel", "圆角" })
            Assert.Contains(must, deg);
        Assert.NotEmpty(d.Doc.Source.Degraded);

        // 颜色没解：Ink/Stroke 一律 null，且不许有对象谎称自己有颜色
        Assert.All(d.Doc.Flattened(), o =>
        {
            Assert.Null(o.Ink);
            Assert.Null(o.Stroke);
        });
        // 字号链串上的那几只，不许填空壳（有 Font 却没字号也没名字＝谎报）
        Assert.All(d.Doc.Flattened(), o =>
        {
            if (o.Text?.Font is not { } f) return;
            Assert.True(f.SizePt is > 0 || !string.IsNullOrEmpty(f.Name),
                $"{baseName}：填了 Font 却既没字号也没名字，这是空壳");
        });
        // 两张参照表都没登记过的 fourcc：有就必须出现在 Degraded 里点名，一条都不许吞
        if (d.UnrecognizedFourcc.Count > 0)
        {
            Assert.Contains(d.Doc.Source.Degraded,
                s => s.Contains("两张参照表", StringComparison.Ordinal) &&
                     d.UnrecognizedFourcc.All(n => s.Contains(n, StringComparison.Ordinal)));
        }
    }

    [Theory]
    [MemberData(nameof(SampleNames))]
    public void Object_order_and_z_follow_the_file_tree_like_A(string baseName)
    {
        var d = Parse(baseName);
        var a = ReadChannelA(baseName);
        Assert.Equal(AObjects(a).Count, BObjects(d.Doc).Count);
        Assert.Equal(AObjects(a).Select(o => o.Z), BObjects(d.Doc).Select(o => o.Z));   // 同一父下从 1 数起
        Assert.Equal(AObjects(a).Select(o => o.Kind), BObjects(d.Doc).Select(o => o.Kind));
        Assert.True(d.Doc.FormatVersion <= CdrxFormat.Version);
        Assert.Equal("cdr-binary", d.Doc.Source.Kind);
    }

    [Fact]
    public void Binary_product_round_trips_through_the_same_cdrx_consumer()
    {
        // B 通道产 CdrxDoc，与 .cdrx.json 走同一个消费方：写出去再读回来必须还在
        var d = Parse("7.8金沐唛头");
        var dir = Path.Combine(Path.GetTempPath(), "labelgou-cdrb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "7.8金沐唛头.cdrx.json");
            File.WriteAllText(path, JsonSerializer.Serialize(d.Doc, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            var back = CdrxReader.ReadFile(path);
            Assert.Equal(2, back.Flattened().Count());
            Assert.Equal(AObjects(ReadChannelA("7.8金沐唛头")).Select(o => o.Text?.Contents),
                AObjects(back).Select(o => o.Text?.Contents));
            Assert.Equal(d.Doc.Source.Degraded, back.Source.Degraded);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Unreadable_cdr_fails_loudly_instead_of_returning_an_empty_doc()
    {
        var junk = new byte[64];
        for (var i = 0; i < junk.Length; i++) junk[i] = 0x20;
        Assert.Throws<InvalidDataException>(() => CdrBinaryParser.ParseBytesDetailed(junk, "假文件.cdr"));

        var zip = EmptyZipWithoutRiffData();
        Assert.Throws<InvalidDataException>(() => CdrBinaryParser.ParseBytesDetailed(zip, "缺条目.cdr"));
    }

    static byte[] EmptyZipWithoutRiffData()
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var e = zip.CreateEntry("metadata/metadata.xml");
            using var w = e.Open();
            w.Write("<x/>"u8);
        }
        return ms.ToArray();
    }

    static string Json(string? s) => JsonSerializer.Serialize(s);
}
