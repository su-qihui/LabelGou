using System.Text.Json.Nodes;
using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;

namespace LabelGou.Core.Agent;

/// <summary>
/// 「读这张表」那一步要外部 agent 交回来的那份 JSON 长什么样（<c>--output-schema</c> 的文件内容）。
/// <para><strong>两份清单都不许手抄</strong>：可选字段键来自 <see cref="MarkFieldCatalog.Mappable"/>，
/// 可选动作来自 <see cref="AiSheetQuestion.Actions"/>。抄一遍就有一次抄错的机会——
/// 本棒写探针时就把字段名手写成了 <c>Volume</c> 与 <c>Marks</c>（真名是 <c>Measurement</c> 与 <c>Remarks</c>），
/// 那条弯路记在 <c>labelgou-other\_probe\agent-mcp\README.md</c> §4。</para>
/// <para><strong>故意不写 <c>additionalProperties:false</c></strong>：模型在读表阶段仍可能自作主张给出
/// <c>rows</c> 或纸规。让严格性去把整次请求弄失败，不如让既有解析器照结构丢掉并说一句人话
/// （<see cref="AiSheetProposal.Parse"/> 在读表阶段丢弃排版字段）。schema 只负责把要什么说清楚，裁判仍是解析器。</para>
/// </summary>
public static class AgentReadProposalSchema
{
    /// <summary>能出现在 <c>mappings[].field</c> 里的字段键（唯一真源 = 字段清单）。</summary>
    public static IReadOnlyList<string> FieldKeys { get; } =
        MarkFieldCatalog.Mappable.Select(d => d.Key.ToString()).ToList();

    /// <summary>那几类风险的合法 <c>action</c>（真源是 <see cref="AiSheetQuestion.Actions"/>，不另立一份）。</summary>
    public static IReadOnlyList<string> QuestionActions { get; } = AiSheetQuestion.Actions;

    /// <summary>给提示词用的那行逗号清单（与 schema 同源，所以永远不吵架）。</summary>
    public static string FieldKeyList() => string.Join(", ", FieldKeys);

    /// <summary>写成文件喂给外部 runtime 的那份 JSON Schema（中文说明原样上路，见 <see cref="JsonRpcMessage.Wire"/>）。</summary>
    public static string Json() => Build().ToJsonString(JsonRpcMessage.Wire);

    private static JsonObject Build() => new()
    {
        ["type"] = "object",
        ["description"] = "第一步只读表、只指出风险：不要给排版（不要 rows、不要纸规名、不要纸张毫米数）。",
        ["properties"] = new JsonObject
        {
            ["dataCols"] = Int("真正有用的数据列数（不算空白列、不算抄模板那一块）"),
            ["mappings"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = 40,
                ["description"] = "哪一列是哪个字段。拿不准的列不要硬塞：错一列就是数错张数、印错货。",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray { "column", "field" },
                    ["properties"] = new JsonObject
                    {
                        ["column"] = Text("列字母、列号或表头原样都行", 60),
                        ["field"] = Enum(FieldKeys, "只能从字段清单里选，写别的会被丢掉"),
                    },
                },
            },
            ["hasHeader"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "false = 这张表没有列名行，第一行也是货",
            },
            ["headerRow"] = Int("列名在原表第几行（原表行号，从 1 起）", 500),
            ["totalRows"] = IntArray("表尾「合计/TOTAL/小计」这类不该出标签的行，原表行号", 60),
            ["templateSource"] = Text("模板抄在哪一列/哪一块（下一步要去那一列量字有多大）", 60),
            ["qtyColumn"] = Text("每个货出几张纸按哪一列数（列字母、列号或表头原样都行）", 60),
            ["paperText"] = Text("表里自己写的那句纸的话，原样抄过来（没有就省略）", 80),
            ["facts"] = StringArray("你从这张表看出来的判断，一条只说一件事", 5),
            ["warnings"] = StringArray("看出来的毛病但不必他拍板的", 8),
            ["questions"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = 12,
                ["description"] = "只许问白名单里那几类；action 写别的软件接不住，那条会被丢掉。",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray { "text", "action" },
                    ["properties"] = new JsonObject
                    {
                        ["text"] = Text("用中文大白话写，别出现 rows/JSON/毫米这类词", 200),
                        ["no"] = Text("点「不是」时软件的记法", 60),
                        ["yes"] = Text("点「是」时软件的记法", 60),
                        ["action"] = Enum(QuestionActions, "这一类问题对应哪个动作"),
                        ["value"] = Text("这一类要带的值（比如列字母、纸规名）", 60),
                        ["row"] = Int("row-keep 那类必须带的原表行号", 500),
                    },
                },
            },
            ["reason"] = Text("为什么这么判（facts 已经说清了就别再说一遍）", 600),
        },
    };

    private static JsonObject Int(string description, int max = 80) => new()
    {
        ["type"] = "integer",
        ["minimum"] = 1,
        ["maximum"] = max,
        ["description"] = description,
    };

    private static JsonObject Text(string description, int max) => new()
    {
        ["type"] = "string",
        ["maxLength"] = max,
        ["description"] = description,
    };

    private static JsonObject StringArray(string description, int maxItems) => new()
    {
        ["type"] = "array",
        ["maxItems"] = maxItems,
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "string" },
    };

    private static JsonObject IntArray(string description, int maxItems) => new()
    {
        ["type"] = "array",
        ["maxItems"] = maxItems,
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
    };

    private static JsonObject Enum(IReadOnlyList<string> values, string description) => new()
    {
        ["type"] = "string",
        ["enum"] = ToJsonArray(values),
        ["description"] = description,
    };

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add((JsonNode?)JsonValue.Create(value));
        return array;
    }
}
