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
    /// <summary>失败 / 终止归类码（取值见 AgentWorkbench.Shared.AgentRunCode）；成功且无异常收尾时为 null。</summary>
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }

    /// <summary>本次 run 的输入 token；供应商没上报时为 null（不是 0）。M033。</summary>
    public long? InputTokens { get; set; }

    /// <summary>本次 run 的输出 token；供应商没上报时为 null（不是 0）。M033。</summary>
    public long? OutputTokens { get; set; }

    /// <summary>
    /// 这条记录是否覆盖了本次 run 的全部动作。被中断（取消 / 超时）时为 false——
    /// 可能有一条工具调用正在执行、没进步骤序列。M033。
    /// </summary>
    public bool Complete { get; set; } = true;

    /// <summary>停因类别（<c>repeated_tool_calls</c> / <c>token_budget</c> / <c>iteration_budget</c>）；正常收尾为 null。M033。</summary>
    public string? StopKind { get; set; }

    /// <summary>停因的观测值（重复次数 / 已用 token / 已跑轮次）。M033。</summary>
    public long? StopObserved { get; set; }

    /// <summary>停因的对照阈值。M033。</summary>
    public long? StopLimit { get; set; }

    /// <summary>停因的对象（通常是工具名）。M033。</summary>
    public string? StopSubject { get; set; }

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

    /// <summary>该步文本是否被上界截断过；回放据此区分「只看到前 N 字」与「本来就短」。M033。</summary>
    public bool Truncated { get; set; }

    public string CreatedAt { get; set; } = string.Empty;
}
