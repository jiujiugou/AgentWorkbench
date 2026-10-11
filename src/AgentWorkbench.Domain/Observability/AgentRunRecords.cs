namespace AgentWorkbench.Domain;

/// <summary>Agent 一次运行（可观测轴：M014 agent_runs）。</summary>
public sealed class AgentRunRecord
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Status { get; set; } = "succeeded";
    public int Iterations { get; set; }
    public string? Model { get; set; }
    public long? LatencyMs { get; set; }
    public string? Error { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? FinishedAt { get; set; }
}

/// <summary>Agent 运行轨迹单步（可观测轴：M015 agent_steps）。</summary>
public sealed class AgentStepRecord
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public int Iteration { get; set; }
    public string Kind { get; set; } = "assistant";
    public string? Tool { get; set; }
    public string? Arguments { get; set; }
    public string? Result { get; set; }
    /// <summary>工具失败原因（异常类型 + 消息）；成功步为 null。与 <see cref="Result"/> 互斥。</summary>
    public string? Error { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}
