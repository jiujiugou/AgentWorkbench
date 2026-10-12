namespace AgentWorkbench.Observability;

/// <summary>一次执行轨迹中的单步（工具调用或最终答复）。</summary>
/// <remarks><see cref="Error"/> 仅工具步可能非空：工具失败时记异常类型与消息（成功步为空）。</remarks>
public sealed record ExecutionStep(
    int Iteration, string Kind, string? Tool, string? Arguments, string? Result, string? Error = null);

/// <summary>一次完整的执行轨迹（成功或失败），即「Agent 到底做了什么」的完整记录。</summary>
/// <remarks>
/// <see cref="Status"/> 说明「以什么状态结束的」（取值见 <see cref="AgentRunStatus"/>），
/// <see cref="ErrorCode"/> 说明「为什么」（取值见 <see cref="AgentRunCode"/>）。
/// 只有 <see cref="Error"/> 一串人话时，调用方无法据此决定「重试、降级还是提示用户」。
/// </remarks>
public sealed record ExecutionTrace(
    string SessionId,
    string Status,
    int Iterations,
    string? Model,
    long LatencyMs,
    string? Error,
    string? ErrorCode,
    IReadOnlyList<ExecutionStep> Steps)
{
    public const string Succeeded = AgentRunStatus.Succeeded;
    public const string Failed = AgentRunStatus.Failed;

    /// <summary>按给定的终态与归类码构造轨迹（提前终止 / 超时 / 取消 / 失败都走这里）。</summary>
    public static ExecutionTrace Create(
        string sessionId, string status, string? errorCode, int iterations, string? model, long latencyMs,
        string? error, IReadOnlyList<ExecutionStep> steps)
        => new(sessionId, status, iterations, model, latencyMs, error, errorCode, steps);

    /// <summary>模型正常收尾（没有触发任何预算）。</summary>
    public static ExecutionTrace Success(
        string sessionId, int iterations, string? model, long latencyMs, IReadOnlyList<ExecutionStep> steps)
        => Create(sessionId, Succeeded, null, iterations, model, latencyMs, null, steps);

    /// <summary>运行失败；<paramref name="errorCode"/> 为归类码（缺省时只留人话）。</summary>
    public static ExecutionTrace Failure(
        string sessionId, int iterations, string? model, long latencyMs, string error,
        IReadOnlyList<ExecutionStep> steps, string? errorCode = null)
        => Create(sessionId, Failed, errorCode, iterations, model, latencyMs, error, steps);
}
