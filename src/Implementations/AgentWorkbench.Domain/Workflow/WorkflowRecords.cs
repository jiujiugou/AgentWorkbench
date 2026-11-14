namespace AgentWorkbench.Domain;

/// <summary>工作流运行状态（P4）。</summary>
public static class WorkflowRunStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>工作流步骤状态（P4，业务台账；不参与恢复放行）。</summary>
public static class WorkflowStepStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Stale = "stale";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";
}

/// <summary>工作流定义（P4）。</summary>
public sealed class WorkflowDefinitionRecord
{
    public string Id { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Definition { get; set; } = "{}";
    public int Enabled { get; set; } = 1;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>提示词版本（P4）。</summary>
public sealed class PromptVersionRecord
{
    public long Id { get; set; }
    public string PromptKey { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Active { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>工作流运行（P4）。</summary>
public sealed class WorkflowRunRecord
{
    public long Id { get; set; }
    public string WorkflowKey { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public string? PromptVersion { get; set; }
    public string Status { get; set; } = WorkflowRunStatuses.Pending;
    public string? Error { get; set; }
    public long? LatencyMs { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? FinishedAt { get; set; }
}

/// <summary>工作流步骤（P4，业务台账；恢复放行由 MAF checkpoint 决定）。</summary>
public sealed class WorkflowStepRecord
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string StepKey { get; set; } = string.Empty;
    public string StepType { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string Status { get; set; } = WorkflowStepStatuses.Pending;
    public int Attempts { get; set; }
    public string? Input { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }

    /// <summary>本步骤动作的幂等键（runId:stepKey），跨重放去重用。</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>审批单标识（HITL 落地后填）。</summary>
    public string? ApprovalId { get; set; }

    /// <summary>外部系统返回的对象标识，用于超时后查证。</summary>
    public string? ExternalRef { get; set; }
}

/// <summary>动作幂等登记状态。</summary>
public static class WorkflowIdempotencyStates
{
    public const string InFlight = "in_flight";
    public const string Completed = "completed";
    public const string Unknown = "unknown";
}

/// <summary>动作幂等登记表：一个 key 只允许产生一次外部副作用。</summary>
public sealed class WorkflowIdempotencyRecord
{
    public string Key { get; set; } = string.Empty;
    public string State { get; set; } = WorkflowIdempotencyStates.InFlight;
    public string? Output { get; set; }
    public string? ExternalRef { get; set; }
    public string? Reason { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? FinishedAt { get; set; }
}
