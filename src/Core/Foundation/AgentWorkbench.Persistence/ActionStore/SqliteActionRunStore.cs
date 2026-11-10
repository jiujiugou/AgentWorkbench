using System.Globalization;
using System.Text.Json;
using AgentWorkbench.Ports;
using Dapper;

namespace AgentWorkbench.Persistence.ActionStore;

/// <summary>
/// <see cref="IActionRunStore"/> 的 SQLite 实现：重启不丢、并发可证。
///
/// 语义与内存实现完全一致，只换存储介质；<c>ActionRunService</c> 的四道顺序、挂起恢复、
/// 超时只查证一概不动。幂等领取用 <c>INSERT OR IGNORE</c> + request_id 主键，
/// 由数据库唯一约束保证「同一请求号只领取一次副作用」，不再依赖进程内字典。
/// </summary>
public sealed class SqliteActionRunStore : IActionRunStore
{
    private const string LoadCheckpointSql =
        "SELECT request_id AS RequestId, state AS State, progress_blob AS ProgressBlob, at AS At " +
        "FROM action_checkpoints WHERE request_id = @requestId";

    private const string ListCheckpointsSql =
        "SELECT request_id AS RequestId, state AS State, progress_blob AS ProgressBlob, at AS At " +
        "FROM action_checkpoints WHERE state = @state ORDER BY at";

    private const string LoadResultSql =
        "SELECT request_id AS RequestId, status AS Status, code AS Code, " +
        "external_id AS ExternalId, error_code AS ErrorCode, data_json AS DataJson " +
        "FROM action_results WHERE request_id = @requestId";

    private readonly IDbConnectionFactory _connections;

    public SqliteActionRunStore(IDbConnectionFactory connections)
    {
        _connections = connections;
        ActionStoreSchema.Ensure(connections);
    }

    public async ValueTask<RunCheckpoint?> LoadCheckpointAsync(string requestId, CancellationToken ct)
    {
        using var connection = _connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<CheckpointRow>(
            new CommandDefinition(LoadCheckpointSql, new { requestId }, cancellationToken: ct));
        return row is null ? null : ToCheckpoint(row);
    }

    public async ValueTask<IReadOnlyList<RunCheckpoint>> ListCheckpointsAsync(string state, CancellationToken ct)
    {
        using var connection = _connections.Create();
        var rows = await connection.QueryAsync<CheckpointRow>(
            new CommandDefinition(ListCheckpointsSql, new { state }, cancellationToken: ct));
        return rows.Select(ToCheckpoint).ToArray();
    }

    public async ValueTask<RunCheckpoint> SuspendAsync(
        string requestId, string state, string progressBlob, CancellationToken ct)
    {
        var checkpoint = new RunCheckpoint(requestId, state, progressBlob, DateTimeOffset.UtcNow);
        using var connection = _connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO action_checkpoints(request_id, state, progress_blob, at) " +
            "VALUES(@requestId, @state, @progressBlob, @at)",
            new { requestId, state, progressBlob, at = Stamp(checkpoint.At) },
            cancellationToken: ct));
        return checkpoint;
    }

    /// <summary>
    /// 从挂起态恢复：<b>取出即清除</b>。
    ///
    /// <para>
    /// 为什么必须删：<c>action_checkpoints</c> 就是「待办队列」，一条被批准或拒绝后就不再待办。
    /// 早先这里是 <see cref="LoadCheckpointAsync"/> 的别名（只读不删），于是已完成、已失败的动作
    /// 永远留在 <c>GET /api/actions/pending</c> 里——界面出现幽灵待办，用户看到「我明明批过了，它还在」。
    /// 重复点批准虽然会被 <c>action_idempotency</c> 回放挡住、不会重复下单，但那是最后一道兜底，
    /// 不该拿来当队列行为正确。
    /// </para>
    /// <para>
    /// 删除与读取在同一个事务里完成：并发两次恢复，第二次读不到行、返回 null，
    /// 调用方据此返回 <c>not_pending</c>，不会出现两个执行体同时认为「这一单归我」。
    /// </para>
    /// </summary>
    public async ValueTask<RunCheckpoint?> ResumeAsync(string requestId, CancellationToken ct)
    {
        using var connection = _connections.Create();
        using var transaction = connection.BeginTransaction();

        var row = await connection.QuerySingleOrDefaultAsync<CheckpointRow>(
            new CommandDefinition(LoadCheckpointSql, new { requestId }, transaction, cancellationToken: ct));
        if (row is null)
        {
            transaction.Commit();
            return null;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM action_checkpoints WHERE request_id = @requestId",
            new { requestId }, transaction, cancellationToken: ct));
        transaction.Commit();
        return ToCheckpoint(row);
    }

    public async ValueTask<bool> TryClaimIdempotencyAsync(
        string requestId, string? idempotencyKey, CancellationToken ct)
    {
        using var connection = _connections.Create();
        // 单条原子 INSERT：并发下第二次必然被主键挡住并返回 0，不存在「先查再插」的竞态窗口。
        var inserted = await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR IGNORE INTO action_idempotency(request_id, idempotency_key, claimed_at) " +
            "VALUES(@requestId, @idempotencyKey, @at)",
            new { requestId, idempotencyKey, at = Stamp(DateTimeOffset.UtcNow) },
            cancellationToken: ct));
        return inserted == 1;
    }

    public async ValueTask<ActionResult?> LoadResultAsync(string requestId, CancellationToken ct)
    {
        using var connection = _connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<ResultRow>(
            new CommandDefinition(LoadResultSql, new { requestId }, cancellationToken: ct));
        return row is null ? null : ToResult(row);
    }

    public async ValueTask SaveResultAsync(ActionResult result, CancellationToken ct)
    {
        var dataJson = result.Data.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : result.Data.GetRawText();
        using var connection = _connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT OR REPLACE INTO action_results" +
            "(request_id, status, code, external_id, error_code, data_json) " +
            "VALUES(@requestId, @status, @code, @externalId, @errorCode, @dataJson)",
            new
            {
                requestId = result.RequestId,
                status = result.Status,
                code = result.Code,
                externalId = result.ExternalId,
                errorCode = result.ErrorCode,
                dataJson,
            },
            cancellationToken: ct));
    }

    private static RunCheckpoint ToCheckpoint(CheckpointRow row)
        => new(row.RequestId, row.State, row.ProgressBlob, ParseStamp(row.At));

    private static ActionResult ToResult(ResultRow row)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.DataJson) ? "{}" : row.DataJson);
        return new ActionResult(
            row.RequestId,
            row.Status,
            row.Code,
            row.ExternalId,
            row.ErrorCode,
            document.RootElement.Clone());
    }

    private static string Stamp(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // 显式别名映射，不依赖 AddNitroPersistence 里的全局 MatchNamesWithUnderscores。
    private sealed class CheckpointRow
    {
        public string RequestId { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public string ProgressBlob { get; set; } = string.Empty;
        public string At { get; set; } = string.Empty;
    }

    private sealed class ResultRow
    {
        public string RequestId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string? ExternalId { get; set; }
        public string? ErrorCode { get; set; }
        public string DataJson { get; set; } = "{}";
    }
}
