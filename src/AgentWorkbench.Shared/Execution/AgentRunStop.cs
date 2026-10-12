namespace AgentWorkbench.Shared;

/// <summary>
/// 一次工具调用请求（执行控制判断「重复调用指纹」的输入）。
/// </summary>
/// <param name="Iteration">工具轮次，0 起（框架的 <c>FunctionInvocationContext.Iteration</c>）。</param>
/// <param name="Name">工具名。</param>
/// <param name="Arguments">已序列化、键序稳定的参数文本；无参数为 <c>null</c>。</param>
public readonly record struct ToolCallObservation(int Iteration, string Name, string? Arguments);

/// <summary>一次终止判定：为什么停（终态 + 归类码 + 给人看的说明）。</summary>
public sealed record AgentRunStop(string Status, string Code, string Message);

/// <summary>
/// 执行控制守卫：执行环在每次工具调用前询问「要不要提前停」，整轮收尾时询问「是不是被截断了」。
/// </summary>
/// <remarks>
/// 判「重复调用指纹」「token 用量」这类信息只有在环内、工具调用发出时才有；
/// 而执行控制模块又不允许反向依赖工具调用或模型运行时。于是把契约放在这里：
/// 执行控制（AgentLoop）实现，工具调用（ToolExecution）在环内调用，编排层（AgentHarness）装配。
/// </remarks>
public interface IAgentRunGuard
{
    /// <summary>某次工具调用即将执行时询问；返回非 <c>null</c> 表示必须提前终止，该次调用不执行。</summary>
    AgentRunStop? InspectToolCall(ToolCallObservation call);

    /// <summary>整轮正常返回后询问收尾分类；返回非 <c>null</c> 表示预算跑满被强制收口（不是「答完了」）。</summary>
    AgentRunStop? InspectCompletion();
}
