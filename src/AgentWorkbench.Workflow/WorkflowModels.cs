using System.Text.Json.Serialization;

namespace AgentWorkbench.Workflow;

/// <summary>工作流能力位。</summary>
public sealed record WorkflowCapabilities(bool Workflow, IReadOnlyList<string> Steps);

/// <summary>工作流步骤定义。</summary>
public sealed record WorkflowStepDefinition(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("timeoutSeconds")] int TimeoutSeconds = 60,
    [property: JsonPropertyName("maxAttempts")] int MaxAttempts = 1,
    [property: JsonPropertyName("promptKey")] string? PromptKey = null);

/// <summary>工作流定义（从 DB definition JSON 解析）。</summary>
public sealed record WorkflowDefinition(string Key, string Name, IReadOnlyList<WorkflowStepDefinition> Steps);

/// <summary>定义 JSON 传输对象。</summary>
internal sealed record WorkflowDefinitionDocument
{
    [JsonPropertyName("steps")] public List<WorkflowStepDefinition> Steps { get; set; } = [];
}

/// <summary>步骤视图。</summary>
public sealed record WorkflowStepView(
    long Id, string StepKey, string StepType, int OrderIndex, string Status, int Attempts,
    string? Output, string? Error, string? StartedAt, string? FinishedAt);

/// <summary>运行视图。</summary>
public sealed record WorkflowRunView(
    long Id, string WorkflowKey, string DocumentId, string? PromptVersion, string Status, string? Error,
    long? LatencyMs, string CreatedAt, string? FinishedAt, IReadOnlyList<WorkflowStepView> Steps);

/// <summary>步骤执行上下文（只读）。</summary>
public sealed record WorkflowStepContext(
    WorkflowRunRecord Run,
    WorkflowStepRecord Step,
    WorkflowStepDefinition Definition,
    string DocumentId,
    string FileName,
    IReadOnlyList<ChunkRecord> Chunks,
    string? PromptContent,
    IReadOnlyList<WorkflowStepRecord> Steps)
{
    public string FullText => string.Join("\n", Chunks.Select(c => c.Content));

    public string? FindOutput(string stepKey)
        => Steps.FirstOrDefault(s => s.StepKey == stepKey)?.Output;
}

/// <summary>文档不存在。</summary>
public sealed class WorkflowDocumentNotFoundException(string documentId)
    : Exception($"文档不存在：{documentId}");

/// <summary>文档未就绪。</summary>
public sealed class WorkflowDocumentNotReadyException(string documentId, string status)
    : Exception($"文档尚未解析成功：{documentId}（状态 {status}）");

/// <summary>工作流定义不存在。</summary>
public sealed class WorkflowDefinitionNotFoundException(string key)
    : Exception($"工作流定义不存在：{key}");

/// <summary>运行不存在。</summary>
public sealed class WorkflowRunNotFoundException(long runId)
    : Exception($"工作流运行不存在：{runId}");

/// <summary>步骤不存在。</summary>
public sealed class WorkflowStepNotFoundException(long runId, string stepKey)
    : Exception($"工作流步骤不存在：{runId}/{stepKey}");

/// <summary>提示词版本不存在。</summary>
public sealed class WorkflowPromptNotFoundException(string key, string version)
    : Exception($"提示词版本不存在：{key}@{version}");

/// <summary>步骤执行失败（重试耗尽/超时）。</summary>
public sealed class WorkflowStepFailedException(string message, Exception? inner = null)
    : Exception(message, inner);
