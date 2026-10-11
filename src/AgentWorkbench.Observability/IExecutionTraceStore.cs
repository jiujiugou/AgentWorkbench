namespace AgentWorkbench.Observability;

/// <summary>执行轨迹的持久化与回放（M014 agent_runs / M015 agent_steps）。</summary>
public interface IExecutionTraceStore
{
    /// <summary>落库一次执行轨迹，返回 runId。写入失败会抛出（由调用方决定是否容忍）。</summary>
    Task<long> SaveAsync(ExecutionTrace trace, CancellationToken cancellationToken = default);

    /// <summary>按 runId 回放轨迹；不存在返回 <c>null</c>。</summary>
    Task<ExecutionTrace?> GetAsync(long runId, CancellationToken cancellationToken = default);
}
