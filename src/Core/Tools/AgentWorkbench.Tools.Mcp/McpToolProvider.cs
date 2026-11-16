using Microsoft.Extensions.AI;
using AgentWorkbench.Tools.Abstractions;

namespace AgentWorkbench.Tools.Mcp;

/// <summary>
/// 把 MCP server 暴露的工具接进工具目录。工具名已由 <see cref="McpToolRegistry"/> 加上
/// <c>mcp__{server}__</c> 前缀，因此与内置工具混在一起也能看出来源。
/// </summary>
/// <remarks>
/// <para>
/// <b>这里只交出「可以直通模型」的那部分。</b> 判定依据是工具自己的能力画像
/// （<see cref="McpToolCapability.CatalogEligible"/>），不是 server 拉到了什么：
/// 声明只读的、或被 <c>TrustedReadOnly</c> 逐个点名的，才进模型目录；
/// 其余（会写的、没声明能力因而不被信任的）留在注册表里，只能由动作闸背后的执行体按名字调用。
/// </para>
/// <para>
/// 为什么要卡在这一层：MCP 工具一旦出现在模型目录里，模型就会直接调它，
/// 而直接调用不经过 <c>ActionGateway</c> 的四道闸（Schema / 权限 / 业务规则 / 审批）。
/// 所以「暴露给模型的能力」必须等于「我们声明过、且没有副作用的那些」，
/// 不能等于「这个 server 装了什么」。
/// </para>
/// <para>
/// 这一层不做白名单判断——<c>Harness:Tools:Whitelist</c> 会再裁一遍。
/// 两道关的分工：本层决定「有没有资格被看见」，白名单决定「模型实际能看见哪些」。
/// 前者随工具能力变化，后者是安全边界，改它必须是一次显式动作。
/// </para>
/// </remarks>
public sealed class McpToolProvider(McpToolRegistry registry) : IAgentToolProvider
{
    public IReadOnlyList<AITool> GetTools()
    {
        var capabilities = registry.Capabilities;
        return registry.Tools
            .Where(tool => capabilities.TryGetValue(tool.Name, out var capability) && capability.CatalogEligible)
            .ToList();
    }
}
