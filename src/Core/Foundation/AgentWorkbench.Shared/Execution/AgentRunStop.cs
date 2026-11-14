namespace AgentWorkbench.Shared;

/// <summary>
/// 一次工具调用请求（执行控制判断「重复调用指纹」的输入）。
/// </summary>
/// <param name="Iteration">工具轮次，0 起（框架的 <c>FunctionInvocationContext.Iteration</c>）。</param>
/// <param name="Name">工具名。</param>
/// <param name="Arguments">已序列化、键序稳定的参数文本；无参数为 <c>null</c>。</param>
public readonly record struct ToolCallObservation(int Iteration, string Name, string? Arguments);

/// <summary>
/// 一次停因的结构化依据：停在哪一类、观测到的值、对照的阈值、对象。
/// </summary>
/// <remarks>
/// 为什么不能只留一句中文：同一个 <c>loop_detected</c>，「连续 3 次」里的 3 是观测值、「阈值 3」是配置，
/// 调用方要据此判断「是不是阈值配得太松」——只能从字符串里正则抠，等于没有。
/// <para><b>生产者是判定方</b>（执行控制 / 结果否决），本结构只是搬运的形状，不做二次计算。
/// <see cref="Observed"/> 与 <see cref="Limit"/> 都是「与判据同单位」的整数：次数、轮次或 token 数。</para>
/// </remarks>
/// <param name="Kind">停因类别（稳定标识，如 <c>repeated_tool_calls</c> / <c>token_budget</c> / <c>iteration_budget</c>）。</param>
/// <param name="Observed">实际观测到的值（重复次数 / 已用 token / 已跑轮次）。</param>
/// <param name="Limit">对照的阈值（配置里的上限）。</param>
/// <param name="Subject">被观测的对象（通常是工具名）；与具体对象无关时为 <c>null</c>。</param>
public sealed record RunStopDetail(string Kind, long Observed, long Limit, string? Subject = null);

/// <summary>一次终止判定：为什么停（终态 + 归类码 + 给人看的说明 + 给机器读的依据）。</summary>
/// <param name="Detail">
/// 结构化依据。判定方拿不到可量化的依据时（例如结果否决只说「依据不足」）为 <c>null</c>；
/// 为 <c>null</c> 不等于「没有依据」，只表示这次停不是数值触发的。
/// </param>
public sealed record AgentRunStop(
    string Status, string Code, string Message, RunStopDetail? Detail = null);

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
