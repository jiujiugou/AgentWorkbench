using Dapper;

namespace AgentWorkbench.Persistence;

/// <summary>一次动作登记的快照。</summary>
public sealed record IdempotencyLease(string Key, string State, string? Output, string? ExternalRef);

/// <summary>
/// 动作幂等登记：MAF checkpoint 恢复会重新投递 pending events，框架只保证「至少一次」。
/// 去重表是框架不给的业务事实层，用来把「至少一次」收敛成「最多一次」。
/// </summary>
public interface IWorkflowIdempotencyStore
{
    /// <summary>原子登记 key；已存在时返回既有状态，不新建。</summary>
    Task<IdempotencyLease> AcquireAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>动作成功，记录产物与外部对象标识。</summary>
    Task CompleteAsync(string key, string? output, string? externalRef, CancellationToken cancellationToken = default);

    /// <summary>副作用状态未知（超时/中断），封锁自动重放，等待查证。</summary>
    Task MarkUnknownAsync(string key, string reason, CancellationToken cancellationToken = default);

    /// <summary>查证完成：把 unknown 解成 completed，后续重放直接复用产物。</summary>
    Task ResolveAsync(string key, string? output, string? externalRef, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>用 <c>INSERT OR IGNORE</c> 而不是「先查再插」：并发重放时只有一次插入生效。</remarks>
public sealed class WorkflowIdempotencyStore(IDbConnectionFactory connections) : IWorkflowIdempotencyStore
{
    // 显式别名：不能依赖 AddNitroPersistence 里那句全局
    // Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true，否则独立宿主会静默丢掉 external_ref。
    private const string SelectSql =
        "SELECT key AS Key, state AS State, output AS Output, external_ref AS ExternalRef, " +
        "reason AS Reason, created_at AS CreatedAt, finished_at AS FinishedAt " +
        "FROM workflow_idempotency WHERE key = @key";

    public async Task<IdempotencyLease> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        using var connection = connections.Create();
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO workflow_idempotency(key, state, created_at) " +
            "VALUES(@key, 'in_flight', @now)",
            new { key, now = Now() },
            cancellationToken: cancellationToken));

        if (inserted == 0)
        {
            // 上一次执行开始过却没走到 Complete：副作用可能已经发生，状态未知。
            // 重放一律不自动再写，转成 unknown 等查证（ADR 0016 B2）。
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE workflow_idempotency SET state = 'unknown', reason = @reason, finished_at = @now " +
                "WHERE key = @key AND state = 'in_flight'",
                new
                {
                    key,
                    reason = "上一次执行未正常完成，副作用状态未知，需查证",
                    now = Now()
                },
                cancellationToken: cancellationToken));
        }

        var row = await connection.QuerySingleAsync<WorkflowIdempotencyRecord>(
            new CommandDefinition(SelectSql, new { key }, cancellationToken: cancellationToken));
        return ToLease(row);
    }

    public async Task CompleteAsync(
        string key, string? output, string? externalRef, CancellationToken cancellationToken = default)
    {
        using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE workflow_idempotency SET state = 'completed', output = @output, " +
            "external_ref = @externalRef, reason = NULL, finished_at = @now " +
            "WHERE key = @key AND state = 'in_flight'",
            new { key, output, externalRef, now = Now() },
            cancellationToken: cancellationToken));
    }

    public async Task MarkUnknownAsync(string key, string reason, CancellationToken cancellationToken = default)
    {
        using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE workflow_idempotency SET state = 'unknown', reason = @reason, finished_at = @now " +
            "WHERE key = @key AND state = 'in_flight'",
            new { key, reason, now = Now() },
            cancellationToken: cancellationToken));
    }

    public async Task ResolveAsync(
        string key, string? output, string? externalRef, CancellationToken cancellationToken = default)
    {
        using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE workflow_idempotency SET state = 'completed', output = @output, " +
            "external_ref = @externalRef, reason = NULL, finished_at = @now " +
            "WHERE key = @key AND state = 'unknown'",
            new { key, output, externalRef, now = Now() },
            cancellationToken: cancellationToken));
    }

    private static IdempotencyLease ToLease(WorkflowIdempotencyRecord row)
        => new(row.Key, row.State, row.Output, row.ExternalRef);

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");
}
