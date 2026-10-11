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

/// <summary>工作流步骤状态（P4，同时是检查点）。</summary>
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

/// <summary>工作流步骤（P4，检查点）。</summary>
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
}
