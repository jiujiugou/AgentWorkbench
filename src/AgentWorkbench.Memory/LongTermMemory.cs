namespace AgentWorkbench.Memory;

/// <summary>一条长期记忆（落库后可被召回的最小单位）。</summary>
/// <param name="Key">同一作用域内的覆盖键：同 <c>(Scope, Key)</c> 的写入是覆盖而不是新增；为空表示「每次都新增」。</param>
/// <param name="Importance">重要性，用于召回排序（越大越靠前）。</param>
/// <param name="ExpiresAt">隐私保留期；为空表示不过期。</param>
public sealed record MemoryItem(
    string Id,
    MemoryScope Scope,
    string Kind,
    string? Key,
    string Content,
    double Importance,
    string? SourceSessionId,
    long? SourceRunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastUsedAt,
    int UseCount,
    DateTimeOffset? ExpiresAt);

/// <summary>写入请求。<see cref="Key"/> 为空则总是新增，否则同 <c>(Scope, Key)</c> 覆盖。</summary>
public sealed record MemoryWrite(
    MemoryScope Scope,
    string Content,
    string? Key = null,
    string Kind = MemoryKinds.Fact,
    double Importance = 0.5,
    string? SourceSessionId = null,
    long? SourceRunId = null,
    TimeSpan? Retention = null);

/// <summary>
/// 召回请求。
/// </summary>
/// <param name="CandidateK">
/// **取库候选上限**。最终注入几条、每条多长、占多少 token 预算，由 <c>Context</c> 决定 —— 本模块不产出 prompt 文本。
/// </param>
/// <param name="Kinds">类别过滤；为空表示不限。</param>
public sealed record MemoryQuery(
    MemoryScope Scope,
    string? Text = null,
    bool IncludeGlobal = true,
    int CandidateK = 20,
    IReadOnlyList<string>? Kinds = null);

/// <summary>
/// 长期记忆载体：存 / 查 / 忘。只负责**结构化数据**的进出，绝不返回拼好的 prompt 文本，
/// 也不决定注入策略（那是 <c>Context</c> 的职责）。
/// </summary>
public interface ILongTermMemoryStore
{
    /// <summary>写入（或按 <c>(Scope, Key)</c> 覆盖）一条记忆，返回落库后的实际状态。</summary>
    Task<MemoryItem> RememberAsync(MemoryWrite write, CancellationToken cancellationToken = default);

    /// <summary>按作用域（+ 可选 global）召回候选，并按重要性回写使用次数。</summary>
    Task<IReadOnlyList<MemoryItem>> RecallAsync(MemoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>显式遗忘一条记忆；不存在返回 <c>false</c>。</summary>
    Task<bool> ForgetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>删除已过保留期的记忆，返回删除条数。</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}
