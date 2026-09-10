using LabelGou.Core.Data;
using LabelGou.Core.Layout;
using LabelGou.Core.Mapping;
using LabelGou.Core.Marks;
using LabelGou.Core.Numbering;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

public class MappingSuggesterTests
{
    private static readonly string[] Headers =
        { "客户名称", "合同号", "目的港 POD", "毛重", "净重", "体积(CBM)", "件号", "备注" };

    [Fact]
    public void 中文与中英混合表头能自动连到正确字段()
    {
        var profile = MappingSuggester.Suggest(Headers);

        Assert.Equal(0, profile.ColumnIndexOf(MarkFieldKey.Consignee));
        Assert.Equal(1, profile.ColumnIndexOf(MarkFieldKey.ContractNo));
        Assert.Equal(2, profile.ColumnIndexOf(MarkFieldKey.DestinationPort));
        Assert.Equal(3, profile.ColumnIndexOf(MarkFieldKey.GrossWeight));
        Assert.Equal(4, profile.ColumnIndexOf(MarkFieldKey.NetWeight));
        Assert.Equal(5, profile.ColumnIndexOf(MarkFieldKey.Measurement));
        Assert.Equal(6, profile.ColumnIndexOf(MarkFieldKey.CartonNo));
        Assert.Equal(7, profile.ColumnIndexOf(MarkFieldKey.Remarks));
    }

    [Fact]
    public void 英文表头也能连上()
    {
        var profile = MappingSuggester.Suggest(new[] { "C/NO", "G.W.", "N.W.", "MEAS", "POD", "ITEM NO" });

        Assert.Equal(1, profile.ColumnIndexOf(MarkFieldKey.GrossWeight));
        Assert.Equal(2, profile.ColumnIndexOf(MarkFieldKey.NetWeight));
        Assert.Equal(3, profile.ColumnIndexOf(MarkFieldKey.Measurement));
        Assert.Equal(4, profile.ColumnIndexOf(MarkFieldKey.DestinationPort));
        Assert.Equal(5, profile.ColumnIndexOf(MarkFieldKey.ItemNo));
    }

    [Fact]
    public void 一个字段只占一列不重复绑定()
    {
        var profile = MappingSuggester.Suggest(new[] { "合同号", "合同号", "客户" });

        var boundColumns = profile.Mappings.Where(m => m.IsBound).Select(m => m.ColumnIndex).ToList();
        Assert.Equal(boundColumns.Distinct().Count(), boundColumns.Count);
    }

    [Fact]
    public void 真表金沐头三列自动连上货号件数与数量()
    {
        // 逐字复刻 labelgou-CL\7.8 金沐 唛头\7.8 金沐 唛头.xlsx 的表头（含单元格内换行与占位列）。
        // 修前只连上 2 个：件数列谁也没绑上，导致行式模板的 Ctns 那行印不出、也不能按件数展开。
        var profile = MappingSuggester.Suggest(new[]
        {
            "货号\nITEM NO:", "件数\nCTN", "数量\nQTY", "一开四", "列 E", "列 F",
        });

        Assert.Equal(0, profile.ColumnIndexOf(MarkFieldKey.ItemNo));
        Assert.Equal(1, profile.ColumnIndexOf(MarkFieldKey.CartonTotal));
        Assert.Equal(2, profile.ColumnIndexOf(MarkFieldKey.Quantity));
        Assert.Equal(3, profile.BoundCount);
        // 备注列与无表头列不许被当成字段抢走
        Assert.Equal(-1, profile.ColumnIndexOf(MarkFieldKey.CartonNo));
    }

    [Fact]
    public void 最高分列被抢不作废字段它仍应拿到次优列()
    {
        // 钉住贪心分配的先后顺序：旧写法先 usedFields.Add 再判列冲突，于是件号在“货号”列上撞车后
        // 就被标成已用，真正能接它的第二列永远轮不到（旧代码下这一列会被总件数抢走）。
        var profile = MappingSuggester.Suggest(new[] { "CARTON 货号", "carton no 件数" });

        Assert.Equal(0, profile.ColumnIndexOf(MarkFieldKey.ItemNo));
        Assert.Equal(1, profile.ColumnIndexOf(MarkFieldKey.CartonNo));
    }

    [Fact]
    public void 两字母拉丁别名只许精确命中不许抢列()
    {
        // “no” 会出现在几乎任何英文表头里（ITEM NO / CARTON NO / POD NO），拿它做包含匹配
        // 会把货号列当成件号列。修前：仅这一列就会被 CartonNo 抢走。
        var profile = MappingSuggester.Suggest(new[] { "货号 ITEM NO:" });

        Assert.Equal(0, profile.ColumnIndexOf(MarkFieldKey.ItemNo));
        Assert.Equal(-1, profile.ColumnIndexOf(MarkFieldKey.CartonNo));
    }

    [Fact]
    public void 同签名方案命中后可按列标题重连列顺序变化的表()
    {
        var original = MappingSuggester.Suggest(Headers, "顺达装箱单");
        var reordered = new[] { "合同号", "客户名称", "件号", "目的港 POD", "毛重", "净重", "体积(CBM)", "备注" };

        var matched = MappingSuggester.FindBestMatch(reordered, new[] { original });

        Assert.NotNull(matched);
        Assert.Equal("顺达装箱单", matched!.Name);
        Assert.Equal(1, matched.ColumnIndexOf(MarkFieldKey.Consignee));   // 列顺序变了但按标题重连
        Assert.Equal(0, matched.ColumnIndexOf(MarkFieldKey.ContractNo));
    }

    [Fact]
    public void 完全不相关的表头不硬套旧方案()
    {
        var original = MappingSuggester.Suggest(Headers, "顺达装箱单");
        var unrelated = new[] { "员工", "部门", "工龄" };

        Assert.Null(MappingSuggester.FindBestMatch(unrelated, new[] { original }));
    }

    [Fact]
    public void 重合度命中旧方案时整批固定值也要带过来()
    {
        // 客户名 BOLAROM 这类不在表里，只存在方案的 FixedValues 里。
        // 旧的重连路径把 Note/AutoNumberCartons/Numbering 都带了，唯独漏了这一项 →
        // 同一客户下次来单，纸上凭空少一行（批次一-8；App 侧两处同类重连都带了）。
        var original = MappingSuggester.Suggest(Headers, "金沐装箱单");
        original.SetFixedValue(MarkFieldKey.Origin, "MADE IN CHINA");
        original.Numbering = new NumberingRule { Mode = NumberingMode.ForceSequence, Start = 21 };
        var slightlyDifferent = Headers.Append("目的国").ToArray();   // 多一列 → 签名变了但重合度够

        var rebound = MappingSuggester.FindBestMatch(slightlyDifferent, new[] { original });

        Assert.NotNull(rebound);
        Assert.Equal("MADE IN CHINA", rebound!.FixedValueFor(MarkFieldKey.Origin));
        Assert.Equal(21, rebound.Numbering!.Start);            // 另一面：原本就带了的那两项不能改坏
    }
}

public class RecordMapperTests
{
    private static TabularData MakeData(IReadOnlyList<string> headers, params string[][] rows)
        => new("test.csv", "CSV", headers, rows.Select(r => (IReadOnlyList<string>)r).ToList(), 0);

    [Fact]
    public void 正常行映射成记录并保留来源列信息()
    {
        var headers = new[] { "客户", "合同号", "毛重" };
        var data = MakeData(headers, new[] { "WALMART", "SD-001", "18.5" });
        var profile = MappingSuggester.Suggest(headers);

        var result = RecordMapper.Map(data, profile);

        var record = Assert.Single(result.Records);
        Assert.Equal("WALMART", record.GetText(MarkFieldKey.Consignee));
        Assert.Equal(ValueOrigin.ExcelImport, record.Get(MarkFieldKey.Consignee)!.Origin);
        Assert.Contains("第 A 列", record.Get(MarkFieldKey.Consignee)!.SourceRef);
    }

    [Fact]
    public void 千分位整串按整体读不再被当小数()
    {
        // "1,250 KGS" 旧口径读成 1.25:重量白名单的「克当千克」告警跟着漏(第 23 棒审计-13)。
        Assert.True(RecordMapper.TryExtractNumber("1,250 KGS", out var kg));
        Assert.Equal(1250, kg, 6);
        Assert.True(RecordMapper.TryExtractNumber("1,234.56", out var mixed));
        Assert.Equal(1234.56, mixed, 6);
        Assert.True(RecordMapper.TryExtractNumber("12,5", out var decimalComma));   // 小数逗号是既定口径,不动
        Assert.Equal(12.5, decimalComma, 6);
        Assert.True(RecordMapper.TryExtractNumber("12.0", out var dot));
        Assert.Equal(12, dot, 6);
    }

    [Fact]
    public void 行标签按原表行号算_AI剔行后不错位()
    {
        // AI 剔掉原表第 2 行后,后面每条记录的「原表第 X 行」必须走 DataRowRawIndexes
        // (第 23 棒审计-6);没有对应表的旧调用方仍退回旧公式,行为零变化。
        var headers = new[] { "客户", "毛重" };
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "A", "10" },
            new[] { "B", "20" },
            new[] { "C", "30" },
        };
        var data = new TabularData("x.xlsx", "Sheet1", headers, rows, 0,
            dataRowRawIndexes: new[] { 0, 2, 3 });          // 原表第 2 行被 AI 剔了
        var profile = MappingSuggester.Suggest(headers);

        var result = RecordMapper.Map(data, profile);

        Assert.Equal(3, result.Records.Count);
        Assert.Equal("Sheet1 原表第 1 行", result.Records[0].SourceRef);
        Assert.Equal("Sheet1 原表第 3 行", result.Records[1].SourceRef);
        Assert.Equal("Sheet1 原表第 4 行", result.Records[2].SourceRef);
    }

    [Fact]
    public void 负重量报Error并标记需核对()
    {
        var headers = new[] { "客户", "毛重" };
        var data = MakeData(headers, new[] { "WALMART", "-18.5" });
        var profile = MappingSuggester.Suggest(headers);

        var result = RecordMapper.Map(data, profile);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(MarkFieldKey.GrossWeight, issue.Field);
        var record = Assert.Single(result.Records);
        Assert.True(record.Get(MarkFieldKey.GrossWeight)!.NeedsReview);
    }

    [Fact]
    public void 带单位的数字能抽取不误报()
    {
        var headers = new[] { "毛重" };
        var data = MakeData(headers, new[] { "18.5 KGS" });
        var profile = MappingSuggester.Suggest(headers);

        var result = RecordMapper.Map(data, profile);

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void 重量列填文字报Warning()
    {
        var headers = new[] { "毛重" };
        var data = MakeData(headers, new[] { "见装箱单" });
        var profile = MappingSuggester.Suggest(headers);

        var result = RecordMapper.Map(data, profile);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void 没给件号列时按行序自动补件号与总件数()
    {
        var headers = new[] { "客户", "合同号" };
        var data = MakeData(headers, new[] { "A", "C1" }, new[] { "B", "C2" }, new[] { "C", "C3" });
        var profile = MappingSuggester.Suggest(headers);
        profile.AutoNumberCartons = true;

        var result = RecordMapper.Map(data, profile);

        Assert.Equal(3, result.Count);
        Assert.Equal("1", result.Records[0].GetText(MarkFieldKey.CartonNo));
        Assert.Equal("3", result.Records[2].GetText(MarkFieldKey.CartonTotal));
        Assert.Equal(ValueOrigin.Rule, result.Records[0].Get(MarkFieldKey.CartonNo)!.Origin);
    }

    [Fact]
    public void 未映射的列以col前缀保留()
    {
        var headers = new[] { "客户", "托盘号" };
        var data = MakeData(headers, new[] { "WALMART", "PLT-7" });
        var profile = MappingProfile.CreateFor(headers);
        profile.Bind(MarkFieldKey.Consignee, 0, headers);   // 只绑客户，托盘号留空

        var result = RecordMapper.Map(data, profile);

        var custom = result.Records[0].GetCustom("col:托盘号");
        Assert.NotNull(custom);
        Assert.Equal("PLT-7", custom!.Text);
    }
}

public class ProfileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "labelgou-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void 方案能存成JSON再原样读回()
    {
        var store = new ProfileStore(_dir);
        var headers = new[] { "客户", "合同号", "毛重" };
        var profile = MappingSuggester.Suggest(headers, "顺达装箱单");

        var fileName = store.Save(profile);
        var loaded = store.Load(fileName);

        Assert.NotNull(loaded);
        Assert.Equal("顺达装箱单", loaded!.Name);
        Assert.Equal(profile.ColumnIndexOf(MarkFieldKey.GrossWeight), loaded.ColumnIndexOf(MarkFieldKey.GrossWeight));
        Assert.Equal(profile.HeaderSignature, loaded.HeaderSignature);
        Assert.Equal(3, loaded.BoundCount);   // 客户 / 合同号 / 毛重 全部自动连上
    }

    [Fact]
    public void 保存的两个方案都能被列出()
    {
        var store = new ProfileStore(_dir);
        store.Save(MappingSuggester.Suggest(new[] { "客户" }, "方案甲"));
        store.Save(MappingSuggester.Suggest(new[] { "合同号" }, "方案乙"));

        var names = store.ListAll().Select(p => p.Name).ToList();

        Assert.Contains("方案甲", names);
        Assert.Contains("方案乙", names);
    }

    [Fact]
    public void JSON里字段键按名字存储便于人工手改()
    {
        var profile = MappingSuggester.Suggest(new[] { "合同号" });
        var json = System.Text.Json.JsonSerializer.Serialize(profile, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        });

        Assert.Contains("\"ContractNo\"", json);
        Assert.DoesNotContain("\"Field\": 3,", json);   // 不能是裸枚举数字，否则用户改不了
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}

public class TemplateValidatorTests
{
    [Theory]
    [InlineData(BuiltInTemplates.IdStandard)]
    [InlineData(BuiltInTemplates.IdCompact)]
    [InlineData(BuiltInTemplates.IdBilingual)]
    public void 三套内置模板必须零错误通过校验(string id)
    {
        var template = BuiltInTemplates.GetById(id);

        Assert.NotNull(template);
        var issues = TemplateValidator.Validate(template!);
        Assert.False(issues.HasError(), string.Join("；", issues.ErrorMessages()));
    }

    [Fact]
    public void 元素越界必须被拒绝_AI生成版式的安全网()
    {
        var template = BuiltInTemplates.Standard100x80();
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text,
            Text = "{{ContractNo}}",
            X = 60,
            Y = 10,
            Width = 60,        // 60+60 > 100
            Height = 6,
            FontSizePt = 9,
        });

        var issues = TemplateValidator.Validate(template);

        Assert.True(issues.HasError());
        Assert.Contains(issues, i => i.Message.Contains("右侧越界"));
    }

    [Fact]
    public void 引用未知字段必须被拒绝()
    {
        var template = BuiltInTemplates.Standard100x80();
        template.Elements[0] = new TemplateElement
        {
            Kind = ElementKind.Text,
            Text = "{{NotARealField}}",
            X = 5, Y = 5, Width = 90, Height = 15,
            FontSizePt = 12,
        };

        var issues = TemplateValidator.Validate(template);

        Assert.True(issues.HasError());
        Assert.Contains(issues, i => i.Message.Contains("未知字段"));
    }

    [Fact]
    public void 字号夸张与列超界都要拦()
    {
        var template = BuiltInTemplates.Compact60x40();
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "X", X = 3, Y = 3, Width = 10, Height = 5, FontSizePt = 200,
        });

        Assert.True(TemplateValidator.Validate(template).HasError());
    }

    [Fact]
    public void 标签尺寸离谱要拦()
    {
        var template = new LabelTemplate { Name = "怪东西", WidthMm = 5000, HeightMm = 3 };

        Assert.True(TemplateValidator.Validate(template).HasError());
    }

    [Fact]
    public void col前缀与内置量视为合法变量()
    {
        var template = BuiltInTemplates.Standard100x80();
        template.Elements.Clear();
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text,
            Text = "{{col:托盘号}} {{NoXofY}} {{RowIndex}}/{{RecordCount}} {{SourceFile}}",
            X = 5, Y = 5, Width = 90, Height = 10, FontSizePt = 10,
        });

        Assert.False(TemplateValidator.Validate(template).HasError());
    }
}

public class TemplateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "labelgou-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void 模板库先列内置再列用户模板()
    {
        var store = new TemplateStore(_dir);

        Assert.True(store.ListAll().Count >= 3);
        Assert.All(store.ListAll().Take(3), t => Assert.True(t.BuiltIn));
    }

    [Fact]
    public void 校验不过的模板拒绝入库()
    {
        var store = new TemplateStore(_dir);
        var bad = BuiltInTemplates.Standard100x80();
        bad.BuiltIn = false;
        bad.Name = "越界模板";
        bad.Elements.Add(new TemplateElement { Kind = ElementKind.Text, Text = "x", X = 50, Y = 50, Width = 90, Height = 60, FontSizePt = 9 });

        var (saved, _, issues) = store.Save(bad);

        Assert.False(saved);
        Assert.True(issues.HasError());
        Assert.DoesNotContain(store.ListAll(), t => !t.BuiltIn);
    }

    [Fact]
    public void 合法用户模板存得下也读得回_这是M7的AI输出契约()
    {
        var store = new TemplateStore(_dir);
        var template = BuiltInTemplates.Standard100x80().CloneAsUserCopy("我的版式");
        template.Elements.Add(new TemplateElement
        {
            Kind = ElementKind.Text, Text = "唛头备注 {{Remarks}}", X = 5, Y = 34, Width = 90, Height = 6, FontSizePt = 8,
        });

        var (saved, fileName, _) = store.Save(template);
        Assert.True(saved);
        var reloaded = store.GetById(template.Id);

        Assert.NotNull(reloaded);
        Assert.Equal("我的版式", reloaded!.Name);
        Assert.False(reloaded.BuiltIn);
        Assert.Equal(template.Elements.Count, reloaded.Elements.Count);
        Assert.Equal(90d, reloaded.Elements[^1].Width, 3);
        Assert.NotNull(fileName);
    }

    [Fact]
    public void 内置模板不可被覆盖保存也不可删除()
    {
        var store = new TemplateStore(_dir);
        var builtIn = BuiltInTemplates.Standard100x80();

        var (saved, _, _) = store.Save(builtIn);
        Assert.False(saved);
        Assert.False(store.Delete(builtIn.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}

public class LayoutEngineTests
{
    [Fact]
    public void 变量被真实值替换且不残留花括号()
    {
        var layout = LayoutEngine.Build(BuiltInTemplates.Standard100x80(), SampleRecords.StandardSample(),
            new LayoutContext(3, 120, "样例.xlsx"));

        var texts = layout.Items.OfType<TextItem>().ToList();
        Assert.DoesNotContain(texts, t => t.Content.Contains("{{"));
        Assert.Contains(texts, t => t.Content.Contains("WALMART"));
        Assert.Contains(texts, t => t.Content.Contains("SD-2026-0831"));
    }

    [Fact]
    public void 件号渲染成_x_slash_y()
    {
        var layout = LayoutEngine.Build(BuiltInTemplates.Standard100x80(), SampleRecords.StandardSample(),
            new LayoutContext(3, 120));

        Assert.Contains(layout.Items.OfType<TextItem>(), t => t.Content == "C/NOS. 3 / 120");
    }

    [Fact]
    public void 变量全空的要素整条隐藏不留残句()
    {
        var record = MarkRecord.Builder()
            .SetRow(1)
            .Set(MarkFieldKey.Consignee, "ONLY CUSTOMER")
            .Set(MarkFieldKey.ContractNo, "C-1")
            .Build();

        var layout = LayoutEngine.Build(BuiltInTemplates.Standard100x80(), record, new LayoutContext(1, 1));

        // "G.W.:  KG" 这种残句不允许出现在印面上
        Assert.DoesNotContain(layout.Items.OfType<TextItem>(), t => t.Content.StartsWith("G.W.:"));
        Assert.DoesNotContain(layout.Items.OfType<TextItem>(), t => t.Content.StartsWith("Meas.:"));
        Assert.True(layout.HiddenElementCount > 0);
    }

    [Fact]
    public void 件号缺总件数时只印本箱号()
    {
        var record = MarkRecord.Builder().Set(MarkFieldKey.Consignee, "A").Set(MarkFieldKey.CartonNo, "7").Build();
        var layout = LayoutEngine.Build(BuiltInTemplates.Standard100x80(), record, new LayoutContext(7, 50));

        Assert.Contains(layout.Items.OfType<TextItem>(), t => t.Content == "C/NOS. 7");
    }

    [Fact]
    public void 未核对的AI字段会让文本标红并点亮打印闸门()
    {
        var record = MarkRecord.Builder()
            .SetRow(2)
            .Set(MarkFieldKey.Consignee, "WALMART")
            .Set(MarkFieldKey.ContractNo, new MarkValue("SD-9999", ValueOrigin.AiLlm)
            {
                Confidence = 0.42,
                NeedsReview = true,
                Warning = "OCR 与大模型结果不一致",
            })
            .Build();

        var layout = LayoutEngine.Build(BuiltInTemplates.Standard100x80(), record, new LayoutContext(2, 10));

        Assert.True(layout.HasUnconfirmed);
        Assert.Contains(layout.Items.OfType<TextItem>(), t => t.Flagged && t.FlagReason!.Contains("不一致"));
    }

    [Fact]
    public void col变量与自定义列能取到值()
    {
        var layout = LayoutEngine.Build(
            SingleTextTemplate("托盘 {{col:托盘号}}"),
            SampleRecords.StandardSample(),
            new LayoutContext(1, 1));

        Assert.Equal("托盘 PLT-0007", Assert.Single(layout.Items.OfType<TextItem>()).Content);
    }

    [Fact]
    public void 未知字段计入未解析清单_不静默印错()
    {
        var template = SingleTextTemplate("{{ContractNo}} / {{NotAField}}");
        var layout = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1));

        Assert.Contains("NotAField", layout.UnresolvedTokens);
    }

    [Fact]
    public void 版面尺寸保持毫米不被缩放污染()
    {
        var template = BuiltInTemplates.Standard100x80();
        var layout = LayoutEngine.Build(template, SampleRecords.StandardSample(), new LayoutContext(1, 1));

        Assert.Equal(100, layout.WidthMm, 3);
        Assert.Equal(80, layout.HeightMm, 3);
        Assert.All(layout.Items.OfType<TextItem>(), t => Assert.InRange(t.Y, 0, 80));
    }

    private static LabelTemplate SingleTextTemplate(string text) => new()
    {
        Name = "单要素测试模板",
        WidthMm = 100,
        HeightMm = 80,
        BorderMm = 0,
        Elements =
        {
            new TemplateElement
            {
                Kind = ElementKind.Text,
                Text = text,
                X = 5,
                Y = 5,
                Width = 90,
                Height = 10,
                FontSizePt = 10,
            },
        },
    };

    [Fact]
    public void 已绑定的列也能用col直取_否则模板会静默变空()
    {
        // 老 bug（用户 2026-09-10 截图 6 那张白纸）：造数据时**只给"没绑定的列"写 col: 值**
        // （`if (bound.Contains(c)) continue;`），而提示词却对模型说"没连上的列也能用 col: 直取那一列"
        // ——两套口径。模型一旦对**已绑定的列**用它（那版就是 `{{col:ITEM NO}}`、`{{col:QTY}}`），
        // 取值全空 → 那一条整条隐藏 → 严重时整张标签空白。
        var headers = new[] { "ITEM NO", "QTY" };
        var data = new TabularData("x.xlsx", "Sheet1", headers, new[] { new[] { "b5011*16 INVISTUC", "16" } }, 0);
        var profile = MappingSuggester.Suggest(headers);      // 自动绑：ITEM NO→ItemNo、QTY→Quantity

        var record = RecordMapper.Map(data, profile).Records.Single();

        Assert.NotNull(record.GetCustom("col:ITEM NO"));      // 已绑定的列也必须取得到
        Assert.NotNull(record.GetCustom("col:QTY"));
        // 而且走的是**同一条字段处理**：货号 * 后面那截照样按规则切掉（用户定过的规则不许被换种取法绕过去）
        Assert.Equal("b5011", record.GetCustom("col:ITEM NO")!.Text);
    }
}
