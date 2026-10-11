namespace AgentWorkbench.Observability;

/// <summary>一次执行轨迹中的单步（工具调用或最终答复）。</summary>
/// <remarks><see cref="Error"/> 仅工具步可能非空：工具失败时记异常类型与消息（成功步为空）。</remarks>
public sealed record ExecutionStep(
    int Iteration, string Kind, string? Tool, string? Arguments, string? Result, string? Error = null);

/// <summary>一次完整的执行轨迹（成功或失败），即「Agent 到底做了什么」的完整记录。</summary>
public sealed record ExecutionTrace(
    string SessionId,
    string Status,
    int Iterations,
    string? Model,
    long LatencyMs,
    string? Error,
    IReadOnlyList<ExecutionStep> Steps)
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";

    public static ExecutionTrace Success(
        string sessionId, int iterations, string? model, long latencyMs, IReadOnlyList<ExecutionStep> steps)
        => new(sessionId, Succeeded, iterations, model, latencyMs, null, steps);

    public static ExecutionTrace Failure(
        string sessionId, int iterations, string? model, long latencyMs, string error, IReadOnlyList<ExecutionStep> steps)
        => new(sessionId, Failed, iterations, model, latencyMs, error, steps);
}
