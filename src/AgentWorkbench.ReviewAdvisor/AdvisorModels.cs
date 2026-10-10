namespace AgentWorkbench.ReviewAdvisor;

/// <summary>审查结论能力位（前端据此显示按钮）。</summary>
public sealed record AdvisorCapabilities(bool Advisor, bool Llm);

/// <summary>结论生成任务视图。</summary>
public sealed record AdvisorJobView(string Status, string? Error, int ConclusionCount, string? CreatedAt, string? FinishedAt);

/// <summary>审查结论视图。</summary>
public sealed record ReviewConclusionView(
    long Id, long? FindingId, string Status, string Title, string? Conclusion, string? Suggestion, string? Impact,
    string Basis, double Confidence, string Source, long? ChunkId, int? Seq, int? PageFrom, int? PageTo,
    string? Evidence, string? Citations, int? StepsUsed);

/// <summary>Agent Loop 轨迹视图。</summary>
public sealed record ReviewAgentStepView(long Id, long? FindingId, int StepIndex, string Kind, string? Tool, string? Arguments, string? Result);

/// <summary>审查任务不存在（无法生成结论）。</summary>
public sealed class AdvisorRunNotFoundException(long runId) : Exception($"审查任务不存在：{runId}");

/// <summary>审查任务尚未完成（前置不满足）。</summary>
public sealed class AdvisorRunNotReadyException(long runId, string status)
    : Exception($"审查任务尚未成功完成，无法生成结论：{runId}（状态 {status}）");

/// <summary>未配置模型，无法生成结论。</summary>
public sealed class AdvisorLlmNotConfiguredException()
    : Exception("未配置模型，无法生成审查结论");
