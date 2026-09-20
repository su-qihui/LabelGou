using System.Text;
using System.Text.Json.Nodes;

namespace LabelGou.Core.Agent;

/// <summary>
/// 工具清单的唯一装配处。
/// <para>为什么收在一处：同一份能力要喂两个方向——出站时给外部 agent 当「你能干这些」的说明，
/// 入站时当 MCP 的 <c>tools/list</c> 回包。各写一份迟早出现「提示词里有这颗、tools/list 里没有」，
/// 那时外部 agent 会去调一颗不存在的工具，报错还落在对面。</para>
/// </summary>
public sealed class AgentToolRegistry
{
    private readonly List<AgentToolSpec> _tools = new();

    /// <summary>已注册的全部工具（含对外不可见的那些）。</summary>
    public IReadOnlyList<AgentToolSpec> All => _tools;

    /// <summary>
    /// 登记一颗工具。名字重了、schema 不是合法 JSON 对象、说明是空的——<strong>当场抛</strong>，
    /// 不留到运行时让客户端去发现。
    /// </summary>
    public AgentToolRegistry Register(AgentToolSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Name))
            throw new ArgumentException("工具名不能为空");
        if (Exists(spec.Name))
            throw new InvalidOperationException($"工具名重复：{spec.Name}");
        if (string.IsNullOrWhiteSpace(spec.Description))
            throw new InvalidOperationException($"工具 {spec.Name} 没写给模型看的说明");
        AssertSchemaObject(spec);
        _tools.Add(spec);
        return this;
    }

    public bool Exists(string name)
        => _tools.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool TryGet(string name, out AgentToolSpec spec)
    {
        var found = _tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        spec = found!;
        return found is not null;
    }

    /// <summary>这颗工具对这个宿主可见吗。<see cref="AgentToolTrust.NeverExposed"/> 对谁都不许看见。</summary>
    public static bool VisibleTo(AgentToolSpec spec, AgentHostKind host) => spec.Trust switch
    {
        AgentToolTrust.ReadOnly => true,
        AgentToolTrust.NeedsUiHost => host == AgentHostKind.WithUi,
        AgentToolTrust.NeverExposed => false,
        _ => false,
    };

    /// <summary>这个宿主实际能调的那几颗。</summary>
    public IReadOnlyList<AgentToolSpec> AvailableFor(AgentHostKind host)
        => _tools.Where(t => VisibleTo(t, host)).ToList();

    /// <summary>
    /// MCP <c>tools/list</c> 的 result 体：<c>{"tools":[{"name","description","inputSchema"}]}</c>。
    /// <para>inputSchema 要的是<strong>对象</strong>而不是字符串，所以这里把存着的 JSON 文本 parse 进去再吐出来；
    /// 存的东西不合法在 <see cref="Register"/> 就死了，走不到这。</para>
    /// </summary>
    public string ToolsListJson(AgentHostKind host)
    {
        var tools = new JsonArray();
        foreach (var spec in AvailableFor(host))
            tools.Add(new JsonObject
            {
                ["name"] = spec.Name,
                ["description"] = spec.Description,
                ["inputSchema"] = JsonNode.Parse(spec.InputSchemaJson),
            });
        return JsonRpcMessage.ToWireJson(new JsonObject { ["tools"] = tools });
    }

    /// <summary>
    /// 给提示词看的那段说明（出站那一路：让模型知道它有这些工具、什么时候该调）。
    /// 与 <see cref="ToolsListJson"/> 读同一份 <see cref="AvailableFor"/>，所以两边永远对得上。
    /// </summary>
    public string DescribeForPrompt(AgentHostKind host)
    {
        var list = AvailableFor(host);
        if (list.Count == 0) return "（这次没有可调用工具，只把 JSON 答案交回来就行。）";
        var sb = new StringBuilder("你可以调用下面这些工具：\n");
        foreach (var spec in list)
            sb.Append("  - ").Append(spec.Name).Append("：").Append(spec.Description).Append('\n');
        return sb.ToString();
    }

    private static void AssertSchemaObject(AgentToolSpec spec)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(spec.InputSchemaJson);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"工具 {spec.Name} 的 inputSchema 不是合法 JSON：{ex.Message}");
        }
        if (node is not JsonObject)
            throw new InvalidOperationException($"工具 {spec.Name} 的 inputSchema 必须是一个 JSON 对象");
    }
}
