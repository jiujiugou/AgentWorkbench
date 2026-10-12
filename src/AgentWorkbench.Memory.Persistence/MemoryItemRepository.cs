using System.Text;
using Dapper;

namespace AgentWorkbench.Memory.Persistence;

/// <summary>一条记忆的存储形态（时间用 ISO-8601 字符串，与既有仓储一致）。</summary>
public sealed class MemoryItemRecord
{
    public string Id { get; set; } = string.Empty;
    public string ScopeType { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string Kind { get; set; } = "fact";
    public string? MemKey { get; set; }
    public string Content { get; set; } = string.Empty;
    public double Importance { get; set; }
    public string? SourceSessionId { get; set; }
    public long? SourceRunId { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string? LastUsedAt { get; set; }
    public int UseCount { get; set; }
    public string? ExpiresAt { get; set; }
}

/// <summary>召回过滤条件。<see cref="Now"/> 是 ISO-8601，用来排除已过保留期的行。</summary>
public sealed record MemoryItemQuery(
    string ScopeType,
    string ScopeId,
    bool IncludeGlobal,
    string? Text,
    IReadOnlyList<string>? Kinds,
    int Limit,
    string Now);

/// <summary>长期记忆仓储：只做 memory_items 表的读写，作用域/去重语义由上层决定。</summary>
public interface IMemoryItemRepository
{
    Task<MemoryItemRecord?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<MemoryItemRecord?> GetByKeyAsync(
        string scopeType, string scopeId, string memKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入：有 <c>mem_key</c> 且同 <c>(scope_type, scope_id, mem_key)</c> 已存在时覆盖，
    /// 否则新增；<c>mem_key</c> 为 <c>null</c> 时总是新增。返回落库后的实际行。
    /// </summary>
    Task<MemoryItemRecord> UpsertAsync(MemoryItemRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryItemRecord>> QueryAsync(
        MemoryItemQuery query, CancellationToken cancellationToken = default);

    /// <summary>把召回命中的记忆记为「用过」（累加 use_count、刷新 last_used_at）。</summary>
    Task<int> TouchAsync(IReadOnlyList<string> ids, string now, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<int> DeleteExpiredAsync(string now, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SqliteMemoryItemRepository : IMemoryItemRepository
{
    private readonly IDbConnectionFactory _factory;

    public SqliteMemoryItemRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<MemoryItemRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.Create();
        return await connection.QuerySingleOrDefaultAsync<MemoryItemRecord>(
            "SELECT * FROM memory_items WHERE id = @id", new { id });
    }

    public async Task<MemoryItemRecord?> GetByKeyAsync(
        string scopeType, string scopeId, string memKey, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.Create();
        return await connection.QuerySingleOrDefaultAsync<MemoryItemRecord>(
            """
            SELECT * FROM memory_items
            WHERE scope_type = @scopeType AND scope_id = @scopeId AND mem_key = @memKey
            """,
            new { scopeType, scopeId, memKey });
    }

    public async Task<MemoryItemRecord> UpsertAsync(
        MemoryItemRecord record, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.Create();
        // RETURNING * 拿到的是「冲突更新后」的真实行（id / created_at / use_count 保持原值），
        // 所以调用方不必再查一次；mem_key 为 NULL 时唯一索引不冲突，这里等价于纯 INSERT。
        return await connection.QuerySingleAsync<MemoryItemRecord>(
            """
            INSERT INTO memory_items (
                id, scope_type, scope_id, kind, mem_key, content, importance,
                source_session_id, source_run_id, created_at, updated_at, last_used_at, use_count, expires_at)
            VALUES (
                @Id, @ScopeType, @ScopeId, @Kind, @MemKey, @Content, @Importance,
                @SourceSessionId, @SourceRunId, @CreatedAt, @UpdatedAt, @LastUsedAt, @UseCount, @ExpiresAt)
            ON CONFLICT(scope_type, scope_id, mem_key) DO UPDATE SET
                content = excluded.content,
                kind = excluded.kind,
                importance = excluded.importance,
                updated_at = excluded.updated_at,
                expires_at = excluded.expires_at,
                source_session_id = excluded.source_session_id,
                source_run_id = excluded.source_run_id
            RETURNING *
            """,
            record);
    }

    public async Task<IReadOnlyList<MemoryItemRecord>> QueryAsync(
        MemoryItemQuery query, CancellationToken cancellationToken = default)
    {
        var sql = new StringBuilder("SELECT * FROM memory_items WHERE ");
        var parameters = new DynamicParameters();
        parameters.Add("ScopeType", query.ScopeType);
        parameters.Add("ScopeId", query.ScopeId);
        parameters.Add("Now", query.Now);

        sql.Append("(expires_at IS NULL OR expires_at > @Now) AND (");

        // global 行没有 scope_id 语义（恒为空串），所以单独作为一个并列分支，而不是去改 scope_id。
        if (query.IncludeGlobal && query.ScopeType != MemoryScopeTypes.Global)
        {
            sql.Append("(scope_type = @ScopeType AND scope_id = @ScopeId) OR scope_type = @GlobalType");
            parameters.Add("GlobalType", MemoryScopeTypes.Global);
        }
        else
        {
            sql.Append("scope_type = @ScopeType AND scope_id = @ScopeId");
        }

        sql.Append(')');

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            // 转义 % 与 _，否则模型传来的文本里的通配符会把召回放大成「全表返回」。
            sql.Append(""" AND (content LIKE @Like ESCAPE '\' OR COALESCE(mem_key, '') LIKE @Like ESCAPE '\')""");
            parameters.Add("Like", $"%{EscapeLike(query.Text)}%");
        }

        if (query.Kinds is { Count: > 0 })
        {
            sql.Append(" AND kind IN @Kinds");
            parameters.Add("Kinds", query.Kinds);
        }

        sql.Append(" ORDER BY importance DESC, updated_at DESC LIMIT @Limit");
        parameters.Add("Limit", Math.Max(1, query.Limit));

        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<MemoryItemRecord>(sql.ToString(), parameters);
        return rows.ToList();
    }

    public async Task<int> TouchAsync(
        IReadOnlyList<string> ids, string now, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
            return 0;

        using var connection = _factory.Create();
        return await connection.ExecuteAsync(
            "UPDATE memory_items SET use_count = use_count + 1, last_used_at = @now WHERE id IN @ids",
            new { ids, now });
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.Create();
        var deleted = await connection.ExecuteAsync("DELETE FROM memory_items WHERE id = @id", new { id });
        return deleted > 0;
    }

    public async Task<int> DeleteExpiredAsync(string now, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.Create();
        return await connection.ExecuteAsync(
            "DELETE FROM memory_items WHERE expires_at IS NOT NULL AND expires_at <= @now", new { now });
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
