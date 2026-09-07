using LabelGou.Core.Impos;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 整版拼版：网格、落位坐标、旋转省料、裁切/套准线、放不下时的行为。
/// 这些都是印刷对不齐的直接来源，坐标断言按毫米手算，不用引擎自己验自己。
/// </summary>
public class ImpositionEngineTests
{
    private static SheetSpec A4(double gutter = 2, bool allowRotate = true, double margin = 8) => new()
    {
        Id = "test.a4",
        Name = "测试 A4",
        PaperWidthMm = 210,
        PaperHeightMm = 297,
        MarginLeftMm = margin,
        MarginTopMm = margin,
        MarginRightMm = margin,
        MarginBottomMm = margin,
        GutterXMm = gutter,
        GutterYMm = gutter,
        AllowRotate = allowRotate,
        RegistrationMarks = false,
    };

    [Fact]
    public void A4排一百乘八十_旋转后每页四枚()
    {
        var plan = ImpositionEngine.Build(A4(), 100, 80, 9);

        Assert.True(plan.Grid.Rotated);
        Assert.Equal(2, plan.Grid.Columns);
        Assert.Equal(2, plan.Grid.Rows);
        Assert.Equal(4, plan.PerPage);
        Assert.Contains(plan.Issues, i => i.Severity == IssueLevel.Info && i.Message.Contains("旋转"));
    }

    [Fact]
    public void 落位坐标按手算核对()
    {
        var plan = ImpositionEngine.Build(A4(), 100, 80, 9);
        var first = plan.PlacementsOnPage(1);

        Assert.Equal(4, first.Count);
        // 旋转后纸面上的实际占位是 80 宽 × 100 高，间距 2
        Assert.Equal(8, first[0].X, 3);
        Assert.Equal(8, first[0].Y, 3);
        Assert.Equal(80, first[0].Width, 3);
        Assert.Equal(100, first[0].Height, 3);
        Assert.Equal(90, first[1].X, 3);            // 第二列：8 + (80+2)
        Assert.Equal(8, first[1].Y, 3);
        Assert.Equal(8, first[2].X, 3);
        Assert.Equal(110, first[2].Y, 3);           // 第二行：8 + (100+2)
        Assert.All(first, p => Assert.Equal(1, p.PageIndex));
        Assert.Equal(Enumerable.Range(1, 4), first.Select(p => p.LabelIndex));
    }

    [Fact]
    public void 页数与末页余量对得上()
    {
        var plan = ImpositionEngine.Build(A4(), 100, 80, 9);

        Assert.Equal(3, plan.PageCount);
        Assert.Equal(1, plan.LabelsLastPage);
        Assert.Equal(3, plan.EmptySlotsLastPage);
        Assert.Single(plan.PlacementsOnPage(3));
        Assert.Equal(4, plan.PlacementsOnPage(2).Count);
        Assert.True(plan.UtilizationPercent is > 38 and < 39);   // 9×8000 / 3×62370 ≈ 38.5%
        Assert.Contains("3 页", plan.Describe());
    }

    [Fact]
    public void 关掉旋转就退回单列三行且提示能省几枚()
    {
        var plan = ImpositionEngine.Build(A4(allowRotate: false), 100, 80, 9);

        Assert.False(plan.Grid.Rotated);
        Assert.Equal(3, plan.PerPage);
        Assert.Equal(4, plan.AlternativePerPage);
        Assert.True(plan.AlternativeRotated);
        Assert.Equal(3, plan.PageCount);
        Assert.Contains("若允许旋转可每页 4 枚", plan.Describe());
    }

    [Fact]
    public void 固定网格按纸规写的行列走()
    {
        var spec = BuiltInSheetSpecs.A4Small60x40();

        var plan = ImpositionEngine.Build(spec, 100, 80, 20);

        // 纸规自带 60×40 时以纸规为准（刀模是物理事实），且不与模板尺寸混用
        Assert.Equal(60, plan.Grid.LabelWidthMm, 3);
        Assert.Equal(3, plan.Grid.Columns);
        Assert.Equal(6, plan.Grid.Rows);
        Assert.Equal(18, plan.PerPage);
        Assert.DoesNotContain(plan.Issues, i => i.Severity == IssueLevel.Error);
        Assert.Contains(plan.Issues, i => i.Severity == IssueLevel.Warning && i.Message.Contains("不一致"));
    }

    [Fact]
    public void 纸规跟模板走时按模板尺寸落位()
    {
        var (w, h) = ImpositionEngine.EffectiveLabelSize(A4(), 100, 80);

        Assert.Equal(100, w, 3);
        Assert.Equal(80, h, 3);
        Assert.True(A4().FollowTemplateSize);
    }

    [Fact]
    public void 标签比可用区还大时不出方案并报错()
    {
        var plan = ImpositionEngine.Build(A4(), 300, 300, 9);

        Assert.Equal(0, plan.PerPage);
        Assert.Empty(plan.Placements);
        Assert.Equal(0, plan.PageCount);
        Assert.Contains(plan.Issues, i => i.Severity == IssueLevel.Error && i.Message.Contains("放不下"));
    }

    [Fact]
    public void 固定列数超过可放数量时被收敛并说明()
    {
        var spec = A4();
        spec.Columns = 10;

        var plan = ImpositionEngine.Build(spec, 100, 80, 9);

        Assert.True(plan.Grid.Columns < 10);
        Assert.Contains(plan.Issues, i => i.Severity == IssueLevel.Error && i.Message.Contains("指定的 10 列"));
    }

    [Fact]
    public void 裁切线默认只画整版四角共八条()
    {
        var spec = A4();
        var plan = ImpositionEngine.Build(spec, 100, 80, 9);

        var marks = ImpositionEngine.BuildMarks(spec, plan, 1).Where(m => m.Kind == SheetMarkKind.CropMark).ToList();

        Assert.Equal(8, marks.Count);
        Assert.All(marks, m =>
        {
            Assert.InRange(m.X1, 0, spec.PaperWidthMm);
            Assert.InRange(m.X2, 0, spec.PaperWidthMm);
            Assert.InRange(m.Y1, 0, spec.PaperHeightMm);
            Assert.InRange(m.Y2, 0, spec.PaperHeightMm);
        });
    }

    [Fact]
    public void 末页裁切范围跟着实际内容缩小()
    {
        var spec = A4();
        var plan = ImpositionEngine.Build(spec, 100, 80, 9);

        var full = ImpositionEngine.BuildMarks(spec, plan, 1).Where(m => m.Kind == SheetMarkKind.CropMark).ToList();
        var last = ImpositionEngine.BuildMarks(spec, plan, 3).Where(m => m.Kind == SheetMarkKind.CropMark).ToList();

        var fullBottom = full.Max(m => Math.Max(m.Y1, m.Y2));
        var lastBottom = last.Max(m => Math.Max(m.Y1, m.Y2));
        Assert.True(lastBottom < fullBottom, "末页只有一枚，裁切线不该按满版位置画");
    }

    [Fact]
    public void 每枚四角模式下线数随枚数增长()
    {
        var spec = A4();
        spec.CropMarks = CropMarkMode.EveryLabel;
        spec.GutterXMm = 12;       // 给角线留出 2×(1+4)=10 mm 的空间，避免重叠告警
        spec.GutterYMm = 12;
        spec.AllowRotate = false;  // 固定取向，保证首页枚数可预期
        var plan = ImpositionEngine.Build(spec, 100, 80, 8);

        var onFirstPage = plan.PlacementsOnPage(1).Count;
        var marks = ImpositionEngine.BuildMarks(spec, plan, 1).Where(m => m.Kind == SheetMarkKind.CropMark).ToList();

        Assert.Equal(3, onFirstPage);
        Assert.Equal(onFirstPage * 8, marks.Count);   // 每枚四角 × 每角两段
    }

    [Fact]
    public void 间距不够时画每枚角线要给告警()
    {
        var spec = A4();
        spec.CropMarks = CropMarkMode.EveryLabel;
        spec.GutterXMm = 2;
        spec.GutterYMm = 2;

        Assert.Contains(SheetSpecValidator.Validate(spec, 100, 80),
            i => i.Severity == IssueLevel.Warning && i.Message.Contains("重叠"));
    }

    [Fact]
    public void 套准十字是四个十字共八条线()
    {
        var spec = A4();
        spec.RegistrationMarks = true;
        var plan = ImpositionEngine.Build(spec, 100, 80, 4);

        var reg = ImpositionEngine.BuildMarks(spec, plan, 1).Where(m => m.Kind == SheetMarkKind.RegistrationMark).ToList();

        Assert.Equal(8, reg.Count);
    }

    [Fact]
    public void 刀模示意线每枚四条()
    {
        var spec = A4();
        spec.LabelOutlineMm = 0.1;
        spec.CropMarks = CropMarkMode.None;
        var plan = ImpositionEngine.Build(spec, 100, 80, 4);

        var outlines = ImpositionEngine.BuildMarks(spec, plan, 1).Where(m => m.Kind == SheetMarkKind.LabelOutline).ToList();

        Assert.Equal(4 * 4, outlines.Count);
    }

    [Fact]
    public void 横向用纸时可用区跟着换向()
    {
        var spec = A4();
        spec.Landscape = true;

        Assert.Equal(297, spec.EffectivePaperWidthMm, 3);
        Assert.Equal(210, spec.EffectivePaperHeightMm, 3);
        Assert.Equal(281, spec.UsableWidthMm, 3);
        Assert.Equal(194, spec.UsableHeightMm, 3);
    }

    [Fact]
    public void 内置纸规种子个个可用()
    {
        foreach (var spec in BuiltInSheetSpecs.All())
        {
            Assert.DoesNotContain(SheetSpecValidator.Validate(spec, 100, 80), i => i.Severity == IssueLevel.Error);
            Assert.NotNull(spec.CloneAsUserCopy("副本"));
        }
    }

    [Fact]
    public void 零张标签时不产生页()
    {
        var plan = ImpositionEngine.Build(A4(), 100, 80, 0);

        Assert.Equal(0, plan.PageCount);
        Assert.Empty(plan.Placements);
        Assert.Equal(0, plan.LabelsLastPage);
        Assert.Empty(ImpositionEngine.BuildMarks(A4(), plan, 1));
    }

    [Fact]
    public void 换纸规比较能算出省几张纸()
    {
        var a4 = ImpositionEngine.Build(A4(), 100, 80, 9);
        var a3 = ImpositionEngine.Build(BuiltInSheetSpecs.A3(), 100, 80, 9);

        Assert.True(a3.PageCount < a4.PageCount);
        Assert.True(ImpositionEngine.ComparePages(a3, a4) > 0);
        Assert.Throws<ArgumentException>(() => ImpositionEngine.ComparePages(a3, ImpositionEngine.Build(A4(), 100, 80, 3)));
    }
}

/// <summary>纸规库的存取与闸门，以及「一页一枚」默认档的几何（套准十字、角线余量、页边下限）。</summary>
public class SheetSpecStoreTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "labelgou-sheets-" + Guid.NewGuid().ToString("N")[..8]);

        public string Path { get; }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Path)) System.IO.Directory.Delete(Path, true);
        }
    }

    [Fact]
    public void 保存后能读回来且排在内置之后()
    {
        using var temp = new TempDir();
        var store = new SheetSpecStore(temp.Path);

        var spec = new SheetSpec { Name = "我店的刀模纸", PaperWidthMm = 210, PaperHeightMm = 297, LabelWidthMm = 90, LabelHeightMm = 60 };
        var (saved, fileName, _) = store.Save(spec);

        Assert.True(saved);
        Assert.NotNull(fileName);

        var all = store.ListAll();
        Assert.Equal(BuiltInSheetSpecs.All().Count + 1, all.Count);
        var loaded = all.Last(s => s.Id == spec.Id);
        Assert.Equal(90, loaded.LabelWidthMm, 3);
        Assert.False(loaded.BuiltIn);
        Assert.True(all.First().BuiltIn, "内置纸规要排在前面");
    }

    [Fact]
    public void 校验不过的纸规拒绝入库()
    {
        using var temp = new TempDir();
        var store = new SheetSpecStore(temp.Path);

        var (saved, _, issues) = store.Save(new SheetSpec { Name = "坏纸规", MarginLeftMm = -5 });

        Assert.False(saved);
        Assert.Contains(issues, i => i.Severity == IssueLevel.Error);
        Assert.Equal(BuiltInSheetSpecs.All().Count, store.ListAll().Count);
    }

    [Fact]
    public void 内置纸规不可覆盖也不可删除()
    {
        using var temp = new TempDir();
        var store = new SheetSpecStore(temp.Path);

        var a4 = BuiltInSheetSpecs.GetById(BuiltInSheetSpecs.IdA4)!;
        var (saved, _, issues) = store.Save(a4);
        Assert.False(saved);
        Assert.Contains(issues, i => i.Message.Contains("内置"));

        Assert.False(store.Delete(BuiltInSheetSpecs.IdA4));
    }

    [Fact]
    public void 内置纸规每次取都是新实例()
    {
        var first = BuiltInSheetSpecs.GetById(BuiltInSheetSpecs.IdA4)!;
        first.MarginLeftMm = 99;

        Assert.NotEqual(99, BuiltInSheetSpecs.GetById(BuiltInSheetSpecs.IdA4)!.MarginLeftMm);
    }

    // ---------- 一页一枚默认档：辅助线不能默默坑在印面上 ----------

    /// <summary>一页一枚：纸 = 唛头 + 2mm 页边（内置种子就是这么定的）。</summary>
    private static SheetSpec OnePer(double margin = 2, bool landscape = false) => new()
    {
        Id = "test.one-per",
        Name = "测试一页一枚",
        FollowsLabel = true,
        Landscape = landscape,
        PaperWidthMm = 144,
        PaperHeightMm = 104,
        MarginLeftMm = margin,
        MarginTopMm = margin,
        MarginRightMm = margin,
        MarginBottomMm = margin,
        GutterXMm = 0,
        GutterYMm = 0,
        Columns = 1,
        Rows = 1,
        AllowRotate = false,
        CropMarks = CropMarkMode.SheetCorners,
        RegistrationMarks = true,
    };

    [Fact]
    public void 一页一枚加横向用纸仍能每页一枚()
    {
        // 旧写法把标签宽加左右边距无条件写进 PaperWidthMm，横向用纸时与轴向错开 90° → 每页 0 枚、一张白纸
        var plan = ImpositionEngine.Build(OnePer(landscape: true), 140, 100, 3);

        Assert.Equal(1, plan.PerPage);
        Assert.Equal(144, plan.PageWidthMm, 3);      // 横向：有效宽 = PaperHeightMm = 140 + 2 + 2
        Assert.Equal(104, plan.PageHeightMm, 3);
        Assert.DoesNotContain(plan.Issues, i => i.Severity == IssueLevel.Error);
    }

    [Fact]
    public void 一页一枚的套准十字不落进标签印面()
    {
        var spec = OnePer();
        var plan = ImpositionEngine.Build(spec, 140, 100, 2);

        var marks = ImpositionEngine.BuildMarks(spec, plan, 1);
        var reg = marks.Where(m => m.Kind == SheetMarkKind.RegistrationMark).ToList();
        var label = plan.PlacementsOnPage(1).Single();

        Assert.All(reg, m => Assert.False(
            m.X1 >= label.X && m.X1 <= label.X + label.Width && m.Y1 >= label.Y && m.Y1 <= label.Y + label.Height,
            $"套准十字 ({m.X1},{m.Y1}) 落在标签印面里"));
        // 纸面只比标签多 2mm，四十字都放不下 → 一个不画，而且要说清为什么
        Assert.Empty(reg);
        Assert.Contains(plan.Issues, i =>
            i.Severity == IssueLevel.Warning && i.Message.Contains("套准十字"));
    }

    [Fact]
    public void 页边够宽时套准十字照旧四个角都画()
    {
        var spec = OnePer(margin: 8);
        spec.RegistrationInsetMm = 3;
        spec.RegistrationSizeMm = 4;
        var plan = ImpositionEngine.Build(spec, 140, 100, 1);

        var reg = ImpositionEngine.BuildMarks(spec, plan, 1).Count(m => m.Kind == SheetMarkKind.RegistrationMark);

        Assert.Equal(8, reg);      // 4 个角 × 每个十字两条线
        Assert.DoesNotContain(plan.Issues, i => i.Message.Contains("套准十字"));
    }

    [Fact]
    public void 角线被页边夹住时不画出一公分的尾巴()
    {
        var spec = OnePer();
        var plan = ImpositionEngine.Build(spec, 140, 100, 1);

        // 旧实现靠 Clamp 把 4mm 的线夹成 1mm 小尾巴（看着像坏了的线）；现在要么按可用外缘收短到能用的长度，要么整角不画
        var crop = ImpositionEngine.BuildMarks(spec, plan, 1).Where(m => m.Kind == SheetMarkKind.CropMark).ToList();
        Assert.All(crop, m =>
        {
            var length = Math.Max(Math.Abs(m.X2 - m.X1), Math.Abs(m.Y2 - m.Y1));
            Assert.True(length < 0.01 || length >= 1.5,
                $"一段角线长 {length:0.###} mm：要么不画，就不能画成看不见的小尾巴");
        });

        // 校验器对 SheetCorners 也补了余量判断（以前只对 EveryLabel）
        Assert.Contains(SheetSpecValidator.Validate(spec, 140, 100), i =>
            i.Severity == IssueLevel.Warning && i.Message.Contains("角线"));
    }

    [Fact]
    public void 页边充足够的角线照旧按全长画()
    {
        var spec = OnePer(margin: 8);
        var plan = ImpositionEngine.Build(spec, 140, 100, 1);

        var crop = ImpositionEngine.BuildMarks(spec, plan, 1).Where(m => m.Kind == SheetMarkKind.CropMark).ToList();

        Assert.Equal(8, crop.Count);
        Assert.All(crop, m => Assert.Equal(spec.CropMarkLengthMm,
            Math.Max(Math.Abs(m.X2 - m.X1), Math.Abs(m.Y2 - m.Y1)), 3));
    }

    [Fact]
    public void 一页一枚的小纸不再被纸张下限报成死胡同()
    {
        // 28×18 的小唛头加 2mm 页边 = 32×22，纸高本来就不到 30mm
        var spec = BuiltInSheetSpecs.GetById(BuiltInSheetSpecs.IdOnePerLabel)!;

        var issues = SheetSpecValidator.Validate(spec, 28, 18);

        Assert.DoesNotContain(issues, i => i.Severity == IssueLevel.Error && i.Message.Contains("小于下限"));
    }

    [Fact]
    public void 普通纸规的纸张下限照旧卡得住()
    {
        var issues = SheetSpecValidator.Validate(new SheetSpec { Name = "坏纸", PaperWidthMm = 28, PaperHeightMm = 18 });

        Assert.Contains(issues, i => i.Severity == IssueLevel.Error && i.Message.Contains("小于下限"));
    }

    [Fact]
    public void 坏纸规文件被跳过时要把文件名与原因报出来()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, "坏了一半.json"), "{ this is not json");
        var store = new SheetSpecStore(temp.Path);

        var report = store.ListWithReport();

        Assert.Equal(BuiltInSheetSpecs.All().Count, report.Specs.Count);
        var skipped = Assert.Single(report.SkippedFiles);
        Assert.Contains("坏了一半.json", skipped, StringComparison.Ordinal);
        Assert.Contains("JSON", skipped, StringComparison.Ordinal);
    }
}
