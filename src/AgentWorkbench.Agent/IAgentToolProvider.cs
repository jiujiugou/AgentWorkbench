using Microsoft.Extensions.AI;

namespace AgentWorkbench.Agent;

/// <summary>
/// Agent 工具扩展点：实现该接口即可向对话 Agent 注入 Microsoft Agent Framework 函数工具，
/// 由 <c>Microsoft.Extensions.AI.AIFunctionFactory</c> 生成的 <see cref="AITool"/> 直接生效，无需改动调用方。
/// </summary>
public interface IAgentToolProvider
{
    IReadOnlyList<AITool> GetTools();
}
