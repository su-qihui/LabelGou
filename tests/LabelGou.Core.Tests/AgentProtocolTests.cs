using System.Text.Json;
using LabelGou.Core.Agent;
using LabelGou.Core.Marks;
using LabelGou.Core.Recognition;
using Xunit;

namespace LabelGou.Core.Tests;

/// <summary>
/// 外部 Agent 那一层的协议判据（第 86 棒）。
/// <para>钉的是三件事：<strong>一帧一行的编解码</strong>（错了对面只会看到"连不上"）、
/// <strong>工具面按宿主收口</strong>（会出纸的东西永远不许被点名）、<strong>MCP 握手的次序</strong>
/// （没 initialize 就别想拿清单）。</para>
/// <para>为什么值得单独一页：这一层是"外部程序能不能改到我们的数据"的唯一边界，而它长在界面之外。
/// §五-151 那一族教训说的是"VM 全绿但机器读不到的那一层"——这里的口径是：判据直接读注册结果与回包文本，
/// 不许读我自己手抄的常量。</para>
/// </summary>
public class AgentProtocolTests
{
    // ───────────────────── JSON-RPC 一帧一行 ─────────────────────

    [Fact]
    public void 带id的请求要回_通知不许回()
    {
        var request = JsonRpcMessage.Parse("""{"jsonrpc":"2.0","id":7,"method":"ping"}""");
        var notice = JsonRpcMessage.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Equal(JsonRpcKind.Request, request.Kind);
        Assert.True(request.WantsReply);
        Assert.Equal(JsonRpcKind.Notification, notice.Kind);
        Assert.False(notice.WantsReply);
    }

    [Fact]
    public void 数字id原样回成数字_字符串id原样回成字符串()
    {
        // 客户端把 "7" 与 7 当成两回事：转一次类型，那条回复就对不上号。
        Assert.Contains("\"id\":7", JsonRpcMessage.WriteResult("7", "{}"));
        Assert.Contains("\"id\":\"abc\"", JsonRpcMessage.WriteResult("abc", "{}"));
    }

    [Theory]
    [InlineData("", "空行")]
    [InlineData("{不是 JSON", "不是合法 JSON")]
    [InlineData("""{"id":1,"method":"ping"}""", "jsonrpc")]        // 缺 2.0 标记
    [InlineData("""{"jsonrpc":"2.0"}""", "既不是请求也不是响应")]   // 没 method 也没 result
    public void 脏帧归为Invalid并带上原因_不抛(string line, string reasonKeyword)
    {
        var message = JsonRpcMessage.Parse(line);
        Assert.Equal(JsonRpcKind.Invalid, message.Kind);
        Assert.Contains(reasonKeyword, message.Error ?? string.Empty);
    }

    [Fact]
    public void 回给我们的响应不会被当成请求()
    {
        var message = JsonRpcMessage.Parse("""{"jsonrpc":"2.0","id":1,"result":{}}""");
        Assert.Equal(JsonRpcKind.Response, message.Kind);
        Assert.False(message.WantsReply);
    }

    // ───────────────────── 工具注册与按宿主收口 ─────────────────────

    static AgentToolSpec Tool(string name, AgentToolTrust trust)
        => new(name, name + " 的说明", """{"type":"object","properties":{}}""", trust);

    static AgentToolRegistry Registry() => new AgentToolRegistry()
        .Register(Tool("labelgou.capabilities", AgentToolTrust.ReadOnly))
        .Register(Tool("labelgou.apply_proposal", AgentToolTrust.NeedsUiHost))
        .Register(Tool("labelgou.print_now", AgentToolTrust.NeverExposed));

    [Fact]
    public void 重名工具当场抛()
        => Assert.Throws<InvalidOperationException>(() => new AgentToolRegistry()
            .Register(Tool("dup", AgentToolTrust.ReadOnly))
            .Register(Tool("DUP", AgentToolTrust.ReadOnly)));

    [Fact]
    public void 入参schema不是对象当场抛()
        => Assert.Throws<InvalidOperationException>(() => new AgentToolRegistry()
            .Register(new AgentToolSpec("bad", "说明", "[1,2]", AgentToolTrust.ReadOnly)));

    [Fact]
    public void 会出纸的工具对两种宿主都不存在()
    {
        foreach (var host in new[] { AgentHostKind.WithUi, AgentHostKind.Headless })
            Assert.DoesNotContain("labelgou.print_now", Registry().AvailableFor(host).Select(t => t.Name));
    }

    [Fact]
    public void 要活界面的工具在不带界面的那一路看不见()
    {
        var headless = Registry().AvailableFor(AgentHostKind.Headless).Select(t => t.Name).ToList();
        Assert.Contains("labelgou.capabilities", headless);
        Assert.DoesNotContain("labelgou.apply_proposal", headless);

        Assert.Contains("labelgou.apply_proposal",
            Registry().AvailableFor(AgentHostKind.WithUi).Select(t => t.Name));
    }

    [Fact]
    public void 清单里的inputSchema是对象而不是字符串()
    {
        using var doc = JsonDocument.Parse(Registry().ToolsListJson(AgentHostKind.WithUi));
        var schema = doc.RootElement.GetProperty("tools")[0].GetProperty("inputSchema");
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
    }

    [Fact]
    public void 提示词里的工具说明与清单读同一份()
    {
        var prompt = Registry().DescribeForPrompt(AgentHostKind.Headless);
        using var doc = JsonDocument.Parse(Registry().ToolsListJson(AgentHostKind.Headless));
        var names = doc.RootElement.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToList();

        Assert.NotEmpty(names);
        foreach (var name in names) Assert.Contains(name, prompt);
    }

    // ───────────────────── MCP 会话次序 ─────────────────────

    static McpServerSession Session(Func<string, JsonElement, AgentToolResult>? call = null)
        => new(Registry(), AgentHostKind.WithUi,
            call ?? ((name, _) => new AgentToolResult(true, name)), "LabelGou", "0.7.0");

    static string Handshake(string protocolVersion = "2025-06-18")
        => """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"@@@"}}"""
            .Replace("@@@", protocolVersion);

    [Fact]
    public void 没握手就要清单_回32002()
    {
        var reply = Session().HandleLine("""{"jsonrpc":"2.0","id":9,"method":"tools/list"}""");
        Assert.Contains("-32002", reply);
    }

    [Fact]
    public void 握手原样回它报的协议版本()
    {
        var reply = Session().HandleLine(Handshake("2024-11-05"));
        Assert.Contains("2024-11-05", reply);
        Assert.Contains("""{"name":"LabelGou","version":"0.7.0"}""", reply);
    }

    [Fact]
    public void 握手之后才列得出工具()
    {
        var session = Session();
        session.HandleLine(Handshake());
        Assert.Contains("labelgou.capabilities",
            session.HandleLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""));
    }

    [Fact]
    public void 调隐藏的工具不会被真调起来()
    {
        var asked = false;
        var session = Session((_, _) => { asked = true; return new AgentToolResult(true, "不该到这"); });
        session.HandleLine(Handshake());

        var reply = session.HandleLine(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"labelgou.print_now","arguments":{}}}""");

        Assert.Contains("没有这颗工具", reply);
        Assert.False(asked);   // 光回一句"没有"不算拦住——真被调起来才是漏
    }

    [Fact]
    public void 工具办不成时回isError而不是协议错误()
    {
        var session = Session((_, _) => new AgentToolResult(false, "这张表还没导入"));
        session.HandleLine(Handshake());

        var reply = session.HandleLine(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"labelgou.capabilities","arguments":{}}}""");

        Assert.Contains(""" "isError":true """.Trim(), reply);
        Assert.Contains("这张表还没导入", reply);
    }

    [Fact]
    public void 工具自己炸了不把内部文字漏出去()
    {
        var session = Session((_, _) => throw new InvalidOperationException("E:\\secret\\path 读不到"));
        session.HandleLine(Handshake());

        var reply = session.HandleLine(
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"labelgou.capabilities","arguments":{}}}""");

        Assert.Contains("-32603", reply);
        Assert.DoesNotContain("secret", reply);
    }

    [Fact]
    public void 请求有回_通知没回()
    {
        var session = Session();
        // 握手那条是请求（带 id），必须有回；后面那条是通知，多回一个字都会把对面的读流打断。
        Assert.NotNull(session.HandleLine(Handshake()));
        Assert.Null(session.HandleLine("""{"jsonrpc":"2.0","method":"notifications/cancelled"}"""));
    }

    [Fact]
    public void 光发initialized通知不算握过手()
    {
        var session = Session();
        session.HandleLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.False(session.IsInitialized);
        Assert.Contains("-32002", session.HandleLine("""{"jsonrpc":"2.0","id":6,"method":"tools/list"}"""));
    }

    // ───────────────────── 超时口径 ─────────────────────

    [Theory]
    [InlineData(0, 0)]      // 0 = 不设限（第 27 棒把 180 硬顶废了）
    [InlineData(-5, 0)]
    [InlineData(3, 10)]     // 低于 10 秒没意义：模型冷启动就要几十秒
    [InlineData(45, 45)]
    public void 超时只有一处算术(int asked, int effective)
        => Assert.Equal(effective, TimeoutPolicy.Effective(asked));

    // ───────────────────── 字段清单与动作清单同源 ─────────────────────

    [Fact]
    public void schema里可选字段与字段清单一字不差()
    {
        var enumList = EnumOf("field");
        var catalog = MarkFieldCatalog.Mappable.Select(d => d.Key.ToString()).ToList();

        Assert.Equal(catalog.Count, enumList.Count);           // 少一个、多一个都红
        foreach (var key in catalog) Assert.Contains(key, enumList);
    }

    [Fact]
    public void schema里可选动作与接得住的动作一字不差()
        => Assert.Equal(AiSheetQuestion.Actions.ToList(), EnumOf("action"));

    [Fact]
    public void 读表那一步的schema里不许出现排版字段()
    {
        // 结构上就不问它要 rows：给了也会被 AiSheetProposal.Parse 丢掉，但 schema 别提这个话头。
        using var doc = JsonDocument.Parse(AgentReadProposalSchema.Json());
        var properties = doc.RootElement.GetProperty("properties");
        Assert.False(properties.TryGetProperty("rows", out _));
        Assert.False(properties.TryGetProperty("paperSpec", out _));
    }

    [Fact]
    public void 逗号清单与schema同源_手写那份不许漂移()
    {
        foreach (var key in MarkFieldCatalog.Mappable.Select(d => d.Key.ToString()))
            Assert.Contains(key, AgentReadProposalSchema.FieldKeyList());
    }

    static List<string> EnumOf(string which)
    {
        using var doc = JsonDocument.Parse(AgentReadProposalSchema.Json());
        var properties = doc.RootElement.GetProperty("properties");
        var node = which == "field"
            ? properties.GetProperty("mappings").GetProperty("items")
                .GetProperty("properties").GetProperty("field").GetProperty("enum")
            : properties.GetProperty("questions").GetProperty("items")
                .GetProperty("properties").GetProperty("action").GetProperty("enum");
        return node.EnumerateArray().Select(e => e.GetString()!).ToList();
    }
}
