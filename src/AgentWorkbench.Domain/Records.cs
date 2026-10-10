namespace AgentWorkbench.Domain;

/// <summary>文档状态机。</summary>
public static class DocumentStatus
{
    public const string Pending = "pending";
    public const string Parsing = "parsing";
    public const string Parsed = "parsed";
    public const string Failed = "failed";
}

public sealed class DocumentRecord
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Status { get; set; } = DocumentStatus.Pending;
    public int PageCount { get; set; }
    public string? Error { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? ParsedAt { get; set; }
}

public sealed class DocumentPageRecord
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public int PageNo { get; set; }
    public string RawText { get; set; } = string.Empty;
    public string CleanedText { get; set; } = string.Empty;
}

public sealed class ChunkRecord
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public int Seq { get; set; }
    public int PageFrom { get; set; }
    public int PageTo { get; set; }
    public string? Heading { get; set; }
    public string Content { get; set; } = string.Empty;
    public int CharCount { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>模型供应商（OpenAI 兼容端点）。API Key 明文存储。</summary>
public sealed class ProviderRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public int IsActive { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>供应商下的可选模型；is_default 表示该供应商当前使用的模型。</summary>
public sealed class ProviderModelRecord
{
    public long Id { get; set; }
    public long ProviderId { get; set; }
    public string Model { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public int IsDefault { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>Agent Harness 一次运行（横切运行时轴）。</summary>
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

/// <summary>Agent Harness 运行轨迹单步。</summary>
public sealed class AgentStepRecord
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public int Iteration { get; set; }
    public string Kind { get; set; } = "assistant";
    public string? Tool { get; set; }
    public string? Arguments { get; set; }
    public string? Result { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>对话会话（通用聊天上下文持久化）。</summary>
public sealed class ConversationRecord
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>会话消息。</summary>
public sealed class MessageRecord
{
    public long Id { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

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
