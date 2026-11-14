using AgentWorkbench.Observability;

namespace AgentWorkbench.Runtime.Harness;

/// <summary>
/// 轨迹 → run 结果的映射。**不属于内核**：它服务的是回放查询（应用层），
/// 所以放在这里只作为「形状转换」的共享实现，<see cref="IAgentHarness"/> 上不提供读入口。
/// </summary>
/// <remarks>
/// 为什么从 <c>IAgentHarness.GetRunAsync</c> 挪出来：内核只需要写口（<see cref="IExecutionTraceWriter"/>），
/// 把「按 runId 取回一次执行」挂在门面上，会让内核为了别人的用途依赖读口。
/// 现在读发生在 Host 的只读端点里，用的是 <see cref="IExecutionTraceReader"/>。
/// </remarks>
public static class HarnessTraceMapping
{
    /// <summary>把一条轨迹还原成调用方看到的 run 结果：最终答复取最后一条 assistant 步（已截断）。</summary>
    public static HarnessRunResult ToResult(long runId, ExecutionTrace trace)
        => new(
            runId,
            trace.SessionId,
            trace.Steps.LastOrDefault(s => s.Kind == "assistant")?.Result ?? string.Empty,
            trace.Iterations,
            trace.Model,
            trace.LatencyMs,
            trace.Steps
                .Select(s => new HarnessStep(
                    s.Iteration, s.Kind, s.Tool, s.Arguments, s.Result, s.Error, s.Truncated))
                .ToList(),
            trace.Status,
            trace.ErrorCode);
}
