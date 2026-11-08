using Dapper;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Memory.Persistence;

/// <summary>一条会话快照的存储形态（时间用 ISO-8601 字符串，与既有仓储一致）。</summary>
public sealed class SessionStateRecord
{
    public string SessionId { get; set; } = string.Empty;

    /// <summary>MAF 序列化后的会话状态（含 <c>InMemoryChatHistoryProvider</c> 的历史）。</summary>
    public string StateJson { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public string? ExpiresAt { get; set; }
}

/// <summary>会话快照仓储：只做 agent_sessions 表的读写，序列化由上层决定。</summary>
public interface ISessionStateRepository
{
    Task<SessionStateRecord?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    Task UpsertAsync(SessionStateRecord record, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>删除 <paramref name="now"/>（ISO-8601 字符串）之前过期的会话行。</summary>
    Task<int> DeleteExpiredAsync(string now, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class SqliteSessionStateRepository : ISessionStateRepository
{
    private readonly IDbConnectionFactory _factory;

    public SqliteSessionStateRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<SessionStateRecord?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.Create();
        return await connection.QuerySingleOrDefaultAsync<SessionStateRecord>(
            "SELECT * FROM agent_sessions WHERE session_id = @sessionId", new { sessionId });
    }

    public async Task UpsertAsync(SessionStateRecord record, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            INSERT INTO agent_sessions (session_id, state_json, created_at, updated_at, expires_at)
            VALUES (@SessionId, @StateJson, @CreatedAt, @UpdatedAt, @ExpiresAt)
            ON CONFLICT(session_id) DO UPDATE SET
                state_json = excluded.state_json,
                updated_at = excluded.updated_at,
                expires_at = excluded.expires_at
            """, record);
    }

    public async Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.Create();
        var deleted = await connection.ExecuteAsync(
            "DELETE FROM agent_sessions WHERE session_id = @sessionId", new { sessionId });
        return deleted > 0;
    }

    public async Task<int> DeleteExpiredAsync(string now, CancellationToken cancellationToken = default)
    {
        using var connection = _factory.Create();
        return await connection.ExecuteAsync(
            "DELETE FROM agent_sessions WHERE expires_at IS NOT NULL AND expires_at <= @now", new { now });
    }
}
