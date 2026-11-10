using Microsoft.Extensions.AI;
using AgentWorkbench.Tools.Abstractions;

namespace AgentWorkbench.Tools.Mcp;

/// <summary>
/// 把 MCP server 暴露的工具接进工具目录。工具名已由 <see cref="McpToolRegistry"/> 加上
/// <c>mcp__{server}__</c> 前缀，因此与内置工具混在一起也能看出来源。
/// </summary>
/// <remarks>
/// 这里不做任何过滤——<c>Harness:Tools:Whitelist</c> 会再裁一遍。
/// 两道关的分工：MCP 配置决定「连哪些 server、拉哪些工具」，白名单决定「模型能看见哪些」。
/// 前者可以随服务器清单变化，后者是安全边界，改它必须是一次显式动作。
/// </remarks>
public sealed class McpToolProvider(McpToolRegistry registry) : IAgentToolProvider
{
    public IReadOnlyList<AITool> GetTools() => registry.Tools;
}
