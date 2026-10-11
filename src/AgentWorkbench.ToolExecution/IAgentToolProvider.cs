namespace AgentWorkbench.ToolExecution;

/// <summary>
/// 工具扩展点：实现该接口即可向 Agent 注入 Microsoft Agent Framework 函数工具，
/// 由 <c>Microsoft.Extensions.AI.AIFunctionFactory</c> 生成的 <see cref="AITool"/> 直接生效，无需改动调用方。
/// 提供方按「一个工具一个类」拆分，便于按工具名定位代码。
/// </summary>
public interface IAgentToolProvider
{
    IReadOnlyList<AITool> GetTools();
}
