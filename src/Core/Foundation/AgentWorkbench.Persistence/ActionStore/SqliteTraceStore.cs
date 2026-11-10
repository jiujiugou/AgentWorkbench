using System.Globalization;
using AgentWorkbench.Ports;
using Dapper;

namespace AgentWorkbench.Persistence.ActionStore;

/// <summary>
/// <see cref="ITraceSink"/> / <see cref="IAuditQuery"/> / <see cref="IActionHistoryQuery"/> 的 SQLite 实现：
/// 重启后审计轨迹仍在。
///
/// 写不进去必须返回 false（fail-closed）：第四道校验依赖这个结论拒绝动作，
/// 数据库异常绝不能被当成「留痕成功」而放行。
/// <c>Available=false</c> 保留下来，用来验证这条门禁在落库实现上同样生效。
/// </summary>
public sealed class SqliteTraceStore : ITraceSink, IAuditQuery, IActionHistoryQuery
{
    private const string SelectSql =
        "SELECT stage AS Stage, outcome AS Outcome, intent AS Intent, arguments AS Arguments, " +
        "approver AS Approver, result AS Result, external_id AS ExternalId, at AS At " +
        "FROM action_trace WHERE request_id = @requestId ORDER BY seq";

    /// <summary>
    /// 列表视图：按「有轨迹即有动作」聚合，左连结果表取终态。
    /// <c>MIN(at)</c> 是这一单第一次落痕的时间（提交时刻）；<c>at</c> 存的是
    /// ISO-8601 round-trip 字符串，字典序即时间序，所以排序可直接用它。
    /// 未收口的动作没有结果行，<c>status</c> 为 NULL —— 不在这里补 pending 判断，
    /// 待办队列由 <c>action_checkpoints</c> 单独提供，两件事不混。
    /// </summary>
    private const string RecentSql =
        "SELECT t.request_id AS RequestId, t.intent AS ActionKey, MIN(t.at) AS At, " +
        "r.status AS Status, r.code AS Code, r.external_id AS ExternalId " +
        "FROM action_trace t LEFT JOIN action_results r ON r.request_id = t.request_id " +
        "GROUP BY t.request_id, t.intent, r.status, r.code, r.external_id " +
        "ORDER BY MIN(t.at) DESC LIMIT @limit";

    private readonly IDbConnectionFactory _connections;
    private int _writeAttempts;

    public SqliteTraceStore(IDbConnectionFactory connections)
    {
        _connections = connections;
        ActionStoreSchema.Ensure(connections);
    }

    /// <summary>false 用于验证「轨迹写不进去必须拒绝」这道门禁，不改变已落库数据。</summary>
    public bool Available { get; set; } = true;

    public int WriteAttempts => Volatile.Read(ref _writeAttempts);

    public async ValueTask<AuditWriteResult> WriteAsync(TraceEvent trace, CancellationToken ct)
    {
        Interlocked.Increment(ref _writeAttempts);
        if (!Available)
            return new AuditWriteResult(false, "trace sink unavailable; fail-closed");

        try
        {
            using var connection = _connections.Create();
            var written = await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO action_trace" +
                "(request_id, stage, outcome, intent, arguments, approver, result, external_id, at) " +
                "VALUES(@requestId, @stage, @outcome, @intent, @arguments, @approver, @result, @externalId, @at)",
                new
                {
                    requestId = trace.RequestId,
                    stage = trace.Stage,
                    outcome = trace.Outcome,
                    intent = trace.Intent,
                    arguments = trace.Arguments,
                    approver = trace.Approver,
                    result = trace.Result,
                    externalId = trace.ExternalId,
                    at = trace.At.ToString("o", CultureInfo.InvariantCulture),
                },
                cancellationToken: ct));
            return new AuditWriteResult(written == 1, "written");
        }
        catch (Exception ex)
        {
            // 不抛给调用方：抛异常会让网关变成「未处理错误」，而不是明确的拒绝。
            return new AuditWriteResult(false, $"trace write failed ({ex.GetType().Name}); fail-closed");
        }
    }

    public async ValueTask<IReadOnlyList<TraceEvent>> AuditTrailAsync(string requestId, CancellationToken ct)
    {
        using var connection = _connections.Create();
        var rows = await connection.QueryAsync<TraceRow>(
            new CommandDefinition(SelectSql, new { requestId }, cancellationToken: ct));
        return rows.Select(row => new TraceEvent(
            requestId,
            row.Stage,
            row.Outcome,
            row.Intent,
            row.Arguments,
            row.Approver,
            row.Result,
            row.ExternalId,
            DateTimeOffset.Parse(row.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)))
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<ActionSummary>> RecentAsync(int limit, CancellationToken ct)
    {
        // 上限兜底：调用方传 0/负数时给一个有限值，避免一次拉全表。
        var take = limit is > 0 and <= 500 ? limit : 50;

        using var connection = _connections.Create();
        var rows = await connection.QueryAsync<SummaryRow>(
            new CommandDefinition(RecentSql, new { limit = take }, cancellationToken: ct));
        return rows.Select(row => new ActionSummary(
            row.RequestId,
            row.ActionKey,
            DateTimeOffset.Parse(row.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            row.Status,
            row.Code,
            row.ExternalId))
            .ToArray();
    }

    private sealed class SummaryRow
    {
        public string RequestId { get; set; } = string.Empty;
        public string ActionKey { get; set; } = string.Empty;
        public string At { get; set; } = string.Empty;
        public string? Status { get; set; }
        public string? Code { get; set; }
        public string? ExternalId { get; set; }
    }

    private sealed class TraceRow
    {
        public string Stage { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public string Intent { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string? Approver { get; set; }
        public string? Result { get; set; }
        public string? ExternalId { get; set; }
        public string At { get; set; } = string.Empty;
    }
}
