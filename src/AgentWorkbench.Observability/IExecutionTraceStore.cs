namespace AgentWorkbench.Observability;

/// <summary>
/// 轨迹查询条件（回放面板与回归测试共用同一份过滤语义——避免「前端一套、测试另一套」两处漂移）。
/// </summary>
/// <param name="SessionId">非空时只返回该会话的轨迹。</param>
/// <param name="Status">非空时只返回该终态的轨迹（取值见 <c>AgentRunStatus</c>）。</param>
/// <param name="Limit">最多返回多少条；结果按 runId 倒序（最近优先）。</param>
public sealed record ExecutionTraceQuery(string? SessionId = null, string? Status = null, int Limit = 50);

/// <summary>执行轨迹的持久化与回放（M014 agent_runs / M015 agent_steps）。</summary>
/// <remarks>
/// <see cref="GetAsync"/> 与 <see cref="QueryAsync"/> 是「轨迹被读」的唯一两条入口：
/// 前者供回放单条（前端回放面板 / 回归测试），后者供按会话或终态检索一批。
/// 两者都必须被运行时或测试消费——只写不读的轨迹是死数据，等于没记。
/// </remarks>
public interface IExecutionTraceStore
{
    /// <summary>落库一次执行轨迹，返回 runId。写入失败会抛出（由调用方决定是否容忍）。</summary>
    Task<long> SaveAsync(ExecutionTrace trace, CancellationToken cancellationToken = default);

    /// <summary>按 runId 回放轨迹；不存在返回 <c>null</c>。</summary>
    Task<ExecutionTrace?> GetAsync(long runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按条件检索历史轨迹（最近优先）。与 <see cref="GetAsync"/> 共用同一份读取语义，
    /// 不存在返回空列表（不是 <c>null</c>——「没查到」与「存储不可用」是两回事）。
    /// </summary>
    Task<IReadOnlyList<ExecutionTrace>> QueryAsync(
        ExecutionTraceQuery query, CancellationToken cancellationToken = default);
}
