namespace AgentWorkbench.Review;

/// <summary>审查上下文：文档元信息 + 管道产物 chunks。</summary>
public sealed record ReviewContext(string DocumentId, string FileName, IReadOnlyList<ChunkRecord> Chunks)
{
    /// <summary>拼接全文（供 LLM 抽取，调用方按需截断）。</summary>
    public string FullText => string.Join("\n", Chunks.Select(c => c.Content));
}

/// <summary>要素抽取草稿（落库前）。</summary>
public sealed record ElementDraft(
    string Key,
    string Label,
    string Value,
    string ValueType,
    string? Unit,
    string Source,
    double Confidence,
    long? ChunkId,
    int? Seq,
    int? PageFrom,
    int? PageTo,
    string? Evidence);

/// <summary>比对清单草稿（落库前）。</summary>
public sealed record FindingDraft(
    long? RuleId,
    string Severity,
    string Category,
    string Title,
    string? Detail,
    string? Expected,
    string? Actual,
    long? ChunkId,
    int? Seq,
    int? PageFrom,
    int? PageTo,
    string? Evidence);

/// <summary>审查能力位（前端据此显示按钮）。</summary>
public sealed record ReviewCapabilities(bool Extract, bool Compare, bool LlmExtract);

/// <summary>审查任务视图。</summary>
public sealed record ReviewRunView(
    long Id,
    string DocumentId,
    string FileName,
    string Mode,
    string? Template,
    string Status,
    string? Model,
    string? Error,
    long? LatencyMs,
    string CreatedAt,
    string? FinishedAt,
    int ElementCount,
    int FindingCount,
    string Steps);

/// <summary>要素视图。</summary>
public sealed record ReviewElementView(
    long Id,
    string Key,
    string Label,
    string Value,
    string ValueType,
    string? Unit,
    string Source,
    double Confidence,
    long? ChunkId,
    int? Seq,
    int? PageFrom,
    int? PageTo,
    string? Evidence);

/// <summary>清单视图。</summary>
public sealed record ReviewFindingView(
    long Id,
    long? RuleId,
    string Severity,
    string Category,
    string Title,
    string? Detail,
    string? Expected,
    string? Actual,
    long? ChunkId,
    int? Seq,
    int? PageFrom,
    int? PageTo,
    string? Evidence);

/// <summary>创建审查请求。</summary>
public sealed record CreateReviewRequest(string DocumentId, string? Mode, string? Template);

/// <summary>文档不存在。</summary>
public sealed class ReviewDocumentNotFoundException(string documentId)
    : Exception($"文档不存在：{documentId}");

/// <summary>文档不可审查（未解析成功）。</summary>
public sealed class ReviewDocumentNotReadyException(string documentId, string status)
    : Exception($"文档尚未解析成功，无法审查：{documentId}（状态 {status}）");

/// <summary>不支持的审查模式（仅 extract / compare / both）。</summary>
public sealed class ReviewInvalidModeException(string mode)
    : Exception($"不支持的审查模式：{mode}（仅支持 extract / compare / both）");
