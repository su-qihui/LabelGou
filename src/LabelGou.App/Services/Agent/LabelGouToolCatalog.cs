using System;
using System.Collections.Generic;
using System.Text.Json;
using LabelGou.Core.Agent;

namespace LabelGou.App.Services.Agent;

/// <summary>
/// 给外部 agent 看的那几颗工具——<strong>只有一份</strong>：LabelGou 自己起外部 agent 时用它的说明，
/// 外部 agent 连进来时也走同一份注册表（§五-62：一份能力两处各写一遍，迟早一边有一边没有）。
/// <para>写与不出纸是这一页的全部要点：能读的一切能读，能写的只有"把一份提案落到当前会话"那一颗，
/// 而它转调的是既有的 <c>ApplyAiProposal</c> 唯一入口（快照照压、可退照退、代数变了照拒）。
/// <strong>打印、导出、存模板在这里根本不存在</strong>——不是"拦下来"，是压根不给。</para>
/// </summary>
internal sealed class LabelGouToolCatalog
{
    /// <summary>当前会话的一张快照（外部客户端看不到我们的内存，只能靠这个）。</summary>
    public sealed record Snapshot(
        string TableDescription,
        int RawRowCount,
        int HeaderRow,
        string? TemplateName,
        string? SheetSpecName);

    /// <summary>把一份提案 JSON 递给既有解析器判（不落活表，只回报"哪几条被丢、哪几条要你拍板"）。</summary>
    public sealed record CheckOutcome(bool Ok, string Report);

    private readonly Func<Snapshot?> _snapshot;
    private readonly Func<string, CheckOutcome> _check;
    private readonly Func<string, (bool Ok, string Message)>? _apply;
    private readonly bool _writesAllowed;

    /// <param name="snapshot">读当前会话；没有表就回 null（工具要如实说"现在没导表"）。</param>
    /// <param name="check">递一份提案来判——<strong>不许在这一格里落地任何东西</strong>。</param>
    /// <param name="apply">落地那一条口（就是 <c>MainWindow.ApplyAiProposal</c>）。null = 这台不给写。</param>
    /// <param name="writesAllowed">老板在设置里点过"允许外部客户端下指令"没有。</param>
    public LabelGouToolCatalog(
        Func<Snapshot?> snapshot,
        Func<string, CheckOutcome> check,
        Func<string, (bool Ok, string Message)>? apply,
        bool writesAllowed)
    {
        _snapshot = snapshot;
        _check = check;
        _apply = apply;
        _writesAllowed = writesAllowed;
    }

    /// <summary>不要参数的工具用的空 schema。</summary>
    private static readonly string EmptyObject = Object();

    /// <summary>
    /// 要一颗 <c>proposalJson</c> 字符串的工具用的 schema。
    /// <para>用 <c>JsonNode</c> 造而不是写一串引号密排的字面量：§五-44 那条教训说的就是把引号封进小函数，
    /// 调用侧看不见转义——本文件第一版拿裸字符串写，收尾少了一个引号直接编不过。</para>
    /// </summary>
    private static readonly string ProposalSchema = Object(("proposalJson", "按给定的 schema 写的那份 JSON"));

    static string Object(params (string Name, string Description)[] properties)
    {
        var props = new System.Text.Json.Nodes.JsonObject();
        foreach (var (name, description) in properties)
            props[name] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string", ["description"] = description };
        var schema = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
        };
        if (properties.Length > 0)
        {
            var required = new System.Text.Json.Nodes.JsonArray();
            foreach (var (name, _) in properties) required.Add(name);
            schema["required"] = required;
        }
        return schema.ToJsonString(JsonRpcMessage.Wire);
    }

    public AgentToolRegistry Build(AgentHostKind host)
    {
        var registry = new AgentToolRegistry()
            .Register(new AgentToolSpec("labelgou.capabilities",
                "这台 LabelGou 能干什么、现在接的是哪一路。先问它，再问别的。",
                EmptyObject, AgentToolTrust.ReadOnly))
            .Register(new AgentToolSpec("labelgou.describe_current_table",
                "看老板当前打开的那张表：原表几行、列名猜在第几行、模板与纸规是哪一份。",
                EmptyObject, AgentToolTrust.ReadOnly))
            .Register(new AgentToolSpec("labelgou.check_proposal",
                "把你写的提案递进来判一次：软件会说清哪几条被丢（读表这一步不许给排版）、哪几条要老板拍板。这一颗不落任何东西。",
                ProposalSchema, AgentToolTrust.ReadOnly));

        // 写的权限只在这一处决定；不可见 = 对面根本看不见这颗，而不是"看见了但不许调"。
        if (host == AgentHostKind.WithUi && _writesAllowed && _apply is not null)
            registry.Register(new AgentToolSpec("labelgou.apply_proposal",
                "把你写的提案落到他当前这份表上。落地前软件会先压快照——他能一键退回来。",
                ProposalSchema, AgentToolTrust.NeedsUiHost));

        return registry;
    }

    /// <summary>执行一颗工具。<see cref="McpServerSession"/> 已经保证只有对它可见的工具才会走到这里。</summary>
    public AgentToolResult Call(string name, JsonElement arguments)
    {
        switch (name)
        {
            case "labelgou.capabilities":
                return new AgentToolResult(true,
                    "LabelGou 唛头标签助手。可读当前表、可判一份提案"
                    + (_writesAllowed && _apply is not null ? "，也可以把提案落到当前会话（改完他能一键撤回）。" : "。写与出纸不在这里做。"));

            case "labelgou.describe_current_table":
            {
                var snap = _snapshot();
                if (snap is null) return new AgentToolResult(false, "现在没有表：他还没走到 ① 导入数据。");
                return new AgentToolResult(true,
                    $"原表 {snap.RawRowCount} 行；列名猜在第 {snap.HeaderRow} 行；" +
                    $"模板「{snap.TemplateName ?? "还没选"}」；纸规「{snap.SheetSpecName ?? "还没选"}」。\n" +
                    snap.TableDescription);
            }

            case "labelgou.check_proposal":
            {
                var json = ReadString(arguments, "proposalJson");
                if (json is null) return new AgentToolResult(false, "少了 proposalJson。");
                var checkedResult = _check(json);
                return new AgentToolResult(checkedResult.Ok, checkedResult.Report);
            }

            case "labelgou.apply_proposal":
            {
                var json = ReadString(arguments, "proposalJson");
                if (json is null) return new AgentToolResult(false, "少了 proposalJson。");
                if (_apply is null) return new AgentToolResult(false, "这台没开写。");
                var (ok, message) = _apply(json);
                return new AgentToolResult(ok, message);
            }

            default:
                return new AgentToolResult(false, $"不认得这颗工具：{name}");
        }
    }

    /// <summary>出问题时给日志用的一行。</summary>
    public string DescribeForLog(AgentHostKind host)
    {
        var names = new List<string>();
        foreach (var tool in Build(host).AvailableFor(host)) names.Add(tool.Name);
        return string.Join("、", names);
    }

    private static string? ReadString(JsonElement arguments, string property)
        => arguments.ValueKind == JsonValueKind.Object
           && arguments.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
