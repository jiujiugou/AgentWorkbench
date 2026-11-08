using Microsoft.Extensions.AI;

namespace AgentWorkbench.Tools.Abstractions;

/// <summary>
/// 工具扩展点：实现该接口即可向 Agent 注入 Microsoft Agent Framework 函数工具，
/// 由 <c>Microsoft.Extensions.AI.AIFunctionFactory</c> 生成的 <see cref="AITool"/> 直接生效，无需改动调用方。
/// 提供方按「一个工具一个类」拆分，便于按工具名定位代码。
/// </summary>
/// <remarks>
/// 本接口是工具环（Runtime.Tools）与所有工具实现方（内置工具、文档、知识、记忆）之间的唯一契约，
/// 单独成工程以避免工具实现方反向依赖整个执行管道。
/// </remarks>
public interface IAgentToolProvider
{
    IReadOnlyList<AITool> GetTools();
}
