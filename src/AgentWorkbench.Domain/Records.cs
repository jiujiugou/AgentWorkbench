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

/// <summary>规则类型（P2 比对用）。</summary>
public static class RuleTypes
{
    public const string ElementRequired = "element_required";
    public const string ClauseRequired = "clause_required";
    public const string ClauseForbidden = "clause_forbidden";
    public const string ValueRule = "value_rule";
}

/// <summary>规则库条目（标准条款 / 风险清单）；P2 接入比对，扩展 rule_type/severity 等。</summary>
public sealed class RuleRecord
{
    public long Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string RuleType { get; set; } = RuleTypes.ClauseRequired;
    public string Severity { get; set; } = "medium";
    public string? TargetKey { get; set; }
    public string? Expected { get; set; }
    public string? MatchHint { get; set; }
    public int Enabled { get; set; } = 1;
    public int Version { get; set; } = 1;
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>审查任务状态。</summary>
public static class ReviewStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>审查模式。</summary>
public static class ReviewModes
{
    public const string Extract = "extract";
    public const string Compare = "compare";
    public const string Both = "both";
}

/// <summary>风险严重级。</summary>
public static class FindingSeverities
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";
    public const string Info = "info";
}

/// <summary>一次审查任务（要素抽取 + 比对）。</summary>
public sealed class ReviewRunRecord
{
    public long Id { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public string Mode { get; set; } = ReviewModes.Both;
    public string? Template { get; set; }
    public string Status { get; set; } = ReviewStatuses.Pending;
    public string Steps { get; set; } = "[]";
    public long? ProviderId { get; set; }
    public string? Model { get; set; }
    public string? Error { get; set; }
    public long? LatencyMs { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? FinishedAt { get; set; }
}

/// <summary>要素抽取结果，带原文证据。</summary>
public sealed class ReviewElementRecord
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string ValueType { get; set; } = "text";
    public string? Unit { get; set; }
    public string Source { get; set; } = "rule";
    public double Confidence { get; set; }
    public long? ChunkId { get; set; }
    public int? Seq { get; set; }
    public int? PageFrom { get; set; }
    public int? PageTo { get; set; }
    public string? Evidence { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>不一致 / 风险清单，带原文证据。</summary>
public sealed class ReviewFindingRecord
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public long? RuleId { get; set; }
    public string Severity { get; set; } = FindingSeverities.Medium;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string? Expected { get; set; }
    public string? Actual { get; set; }
    public long? ChunkId { get; set; }
    public int? Seq { get; set; }
    public int? PageFrom { get; set; }
    public int? PageTo { get; set; }
    public string? Evidence { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>审查结论状态（P3）。</summary>
public static class ConclusionStatuses
{
    public const string Concluded = "concluded";
    public const string Insufficient = "insufficient";
}

/// <summary>结论依据（P3）。</summary>
public static class ConclusionBasis
{
    public const string Evidence = "evidence";
    public const string NoEvidence = "no-evidence";
}

/// <summary>审查结论（P3 Agent Loop 产出），带原文引用。</summary>
public sealed class ReviewConclusionRecord
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public long? FindingId { get; set; }
    public string Status { get; set; } = ConclusionStatuses.Insufficient;
    public string Title { get; set; } = string.Empty;
    public string? Conclusion { get; set; }
    public string? Suggestion { get; set; }
    public string? Impact { get; set; }
    public string Basis { get; set; } = ConclusionBasis.NoEvidence;
    public double Confidence { get; set; }
    public string Source { get; set; } = "llm";
    public long? ChunkId { get; set; }
    public int? Seq { get; set; }
    public int? PageFrom { get; set; }
    public int? PageTo { get; set; }
    public string? Evidence { get; set; }
    public string? Citations { get; set; }
    public string? PromptVersion { get; set; }
    public int? StepsUsed { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>Agent Loop 单步轨迹（P3）。</summary>
public sealed class ReviewAgentStepRecord
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public long? FindingId { get; set; }
    public int StepIndex { get; set; }
    public string Kind { get; set; } = "assistant";
    public string? Tool { get; set; }
    public string? Arguments { get; set; }
    public string? Result { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
}

/// <summary>审查结论生成任务状态（P3）。</summary>
public static class AdvisorJobStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>审查结论生成任务（P3）。</summary>
public sealed class AdvisorJobRecord
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string Status { get; set; } = AdvisorJobStatuses.Pending;
    public string? Error { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? FinishedAt { get; set; }
}

/// <summary>模型供应商（OpenAI 兼容端点）。API Key 明文存储，P2 再加密。</summary>
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
