using System.Linq;
using LabelGou.Core.Recognition;
using LabelGou.Core.Templates;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 「AI 排版」那条路的钉子（M7 第 11 棒）：模型回的原文 → <see cref="RowLayoutJsonParser"/> →
/// <see cref="RowLayoutSpec.Build"/> → <see cref="TemplateValidator"/>。
/// <para>为什么钉在解析这一层而不是界面：用户要的是「AI 排版」，而模型给的东西实测就是脏的——
/// 围栏、解释文字、字符串数字、同义键名、目录里没有的字段。直接反序列化要么抛要么静默丢，
/// 用户看到的就只剩一句「模型没给出可用版式」，跟没做一样。</para>
/// <para>这里同时钉住<strong>不许静默</strong>：每一处被夹、被剥、被截的东西都必须落在 Notes 里，
/// 让人在确认界面上看得见（§五-54 同一条纪律：悄悄修好等于骗人）。</para>
/// </summary>
public class AiLayoutTests
{
    /// <summary>金沐那张真样件的四行形状，模型照抄时最可能给的形状。</summary>
    private const string FourRows = """
        {"name":"金沐四行","widthMm":140,"heightMm":100,"paddingMm":5,"gapMm":2,"drawBorder":false,
         "rows":[{"content":"BOLAROM","weight":1.6,"align":"center","stretch":true},
                 {"content":"Item no：{{ItemNo}}","weight":1,"align":"left","sizePt":14,"stretch":false},
                 {"content":"QTY：{{Quantity}} pcs","weight":1,"align":"left","sizePt":14,"stretch":false},
                 {"content":"Ctns：{{col:本行箱数}}件","weight":1,"align":"left","sizePt":14,"stretch":false}]}
        """;

    [Fact]
    public void 裸JSON围栏与夹带解释文字三种形状都认()
    {
        foreach (var raw in new[]
        {
            FourRows,
            "```json\n" + FourRows + "\n```",
            "好的，我按你给的字段排了一版：\n" + FourRows + "\n如需调整告诉我。",
        })
        {
            var p = RowLayoutJsonParser.Parse(raw);
            Assert.True(p.HasSpec, raw.Substring(0, 20) + " → " + string.Join("；", p.Errors));
            Assert.Equal(4, p.Spec!.Rows.Count);
            Assert.Empty(p.Notes);                      // 这份是干净的，不该编出修正来
        }
    }

    [Fact]
    public void 撑满行与明细行的字号按骨架反算而不是听模型的()
    {
        var spec = RowLayoutJsonParser.Parse(FourRows).Spec!;

        Assert.True(spec.Rows[0].Stretch);
        Assert.Equal(HorizontalAlign.Center, spec.Rows[0].Align);
        Assert.Equal(1.6, spec.Rows[0].Weight, 3);
        Assert.False(spec.Rows[1].Stretch);
        Assert.Equal(14, spec.Rows[1].SizePt, 3);
    }

    [Fact]
    public void 方案能排成模板并且过校验闸门()
    {
        var spec = RowLayoutJsonParser.Parse(FourRows).Spec!;

        var template = spec.Build();
        Assert.NotNull(template);
        var issues = TemplateValidator.Validate(template!);
        Assert.False(issues.HasError(), string.Join("；", issues.Select(i => i.Message)));
        Assert.Equal(4, template!.Elements.Count);     // 四行、无边框那份就该只有四个文字元素
    }

    [Fact]
    public void 字符串数字与同义键名都读得懂也不编造修正()
    {
        var p = RowLayoutJsonParser.Parse("""
            {"名称":"客户版式","width":"140","高":"100","padding":"5","gap":2,
             "rows":[{"text":"MADE IN {{Origin}}","size":"18","weight":"1","align":"居中"}]}
            """);

        Assert.True(p.HasSpec, string.Join("；", p.Errors));
        Assert.Equal(140, p.Spec!.WidthMm, 3);
        Assert.Equal(100, p.Spec!.HeightMm, 3);
        Assert.Equal(18, p.Spec!.Rows[0].SizePt, 3);
        Assert.Equal(HorizontalAlign.Center, p.Spec!.Rows[0].Align);
        Assert.Empty(p.Notes);                                   // 值都在范围内：没越界就不该编出一堆「已修正」
    }

    [Fact]
    public void 目录里没有的字段被剥掉但不留花括号()
    {
        var p = RowLayoutJsonParser.Parse("""
            {"rows":[{"content":"货号：{{ItemNo}} 数量：{{QtyPerCarton}}"}]}
            """);

        Assert.True(p.HasSpec, string.Join("；", p.Errors));
        var content = p.Spec!.Rows[0].Content;
        Assert.Contains("{{ItemNo}}", content);
        Assert.DoesNotContain("QtyPerCarton", content);
        Assert.DoesNotContain("{{", content.Replace("{{ItemNo}}", ""));   // 不许留下半个花括号印到纸上
        Assert.Contains(p.Notes, n => n.Contains("QtyPerCarton"));
    }

    [Fact]
    public void 超过六行截断并如实说明()
    {
        var rows = string.Join(",", Enumerable.Range(1, 9).Select(i => $$"""{"content":"第{{i}}行"}"""));
        var p = RowLayoutJsonParser.Parse("""{"rows":[""" + rows + "]}");

        Assert.True(p.HasSpec, string.Join("；", p.Errors));
        Assert.Equal(RowLayoutJsonParser.MaxRows, p.Spec!.Rows.Count);
        Assert.Contains(p.Notes, n => n.Contains("9 行"));
    }

    [Fact]
    public void 离谱尺寸被夹回可印范围而不是原样收下()
    {
        var p = RowLayoutJsonParser.Parse("""{"widthMm":9000,"heightMm":-5,"rows":[{"content":"NO. {{CartonNo}}"}]}""");

        Assert.True(p.HasSpec, string.Join("；", p.Errors));
        Assert.Equal(TemplateValidator.MaxLabelSideMm, p.Spec!.WidthMm, 3);
        Assert.Equal(TemplateValidator.MinLabelSideMm, p.Spec!.HeightMm, 3);
        Assert.Equal(2, p.Notes.Count);                          // 只有越界的那两个要说明；padding/gap 没提不算错
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("这版我排不出来，抱歉。")]                      // 没有 JSON
    [InlineData("{\"rows\":[{\"content\":\"第一行\"}")]        // 花括号没配平 = 话说一半
    [InlineData("[{\"content\":\"第一行\"}]")]                  // 根不是对象
    [InlineData("{}")]                                          // 没有 rows
    [InlineData("{\"rows\":[]}")]                               // rows 是空的
    [InlineData("{\"rows\":[{\"content\":\"{{FooBar}}\"}]}")]      // 整行只有一个被剥掉的字段，洗完就空了
    public void 排不出来的形状一律拒绝而不是造半份方案(string? raw)
    {
        var p = RowLayoutJsonParser.Parse(raw);

        Assert.False(p.HasSpec);
        Assert.NotEmpty(p.Errors);
    }

    [Fact]
    public void 提示词只给这张表真连上的字段并且不许模型碰毫米()
    {
        var fields = new[] { ("ItemNo", "货号", "olu830-35"), ("Quantity", "每箱数量", "144") };

        var prompt = RowLayoutPrompt.Build(fields, 140, 100, "第四行要印本行箱数");

        Assert.Contains("ItemNo", prompt);
        Assert.Contains("olu830-35", prompt);                       // 样例值要给模型，不然它不知道这列长什么样
        Assert.Contains("只回一个 JSON 对象", prompt);
        Assert.Contains("不要输出任何毫米坐标", prompt);
        Assert.Contains("第四行要印本行箱数", prompt);
        Assert.DoesNotContain("Consignee", prompt);                 // 没连上的字段不许出现在清单里诱导模型去用
    }
}
