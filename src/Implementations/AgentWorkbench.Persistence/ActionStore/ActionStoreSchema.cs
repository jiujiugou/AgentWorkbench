using AgentWorkbench.Persistence;
using Dapper;

namespace AgentWorkbench.Persistence.ActionStore;

/// <summary>
/// 动作 run / 审计四张表的 DDL。<b>单一事实源</b>：M030 迁移与 SQLite 存储构造都读这一份，
/// 避免「迁移建一套、代码里另写一套」漂移。全部 IF NOT EXISTS，重复执行幂等。
/// </summary>
public static class ActionStoreSchema
{
    public static readonly IReadOnlyList<string> Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS action_checkpoints (
            request_id    TEXT PRIMARY KEY NOT NULL,
            state         TEXT NOT NULL,
            progress_blob TEXT NOT NULL,
            at            TEXT NOT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS ix_action_checkpoints_state ON action_checkpoints(state)",
        """
        CREATE TABLE IF NOT EXISTS action_results (
            request_id  TEXT PRIMARY KEY NOT NULL,
            status      TEXT NOT NULL,
            code        TEXT NOT NULL,
            external_id TEXT NULL,
            error_code  TEXT NULL,
            data_json   TEXT NOT NULL
        )
        """,
        // request_id 上的主键就是并发唯一约束：INSERT OR IGNORE 只有一次能生效。
        """
        CREATE TABLE IF NOT EXISTS action_idempotency (
            request_id      TEXT PRIMARY KEY NOT NULL,
            idempotency_key TEXT NULL,
            claimed_at      TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE IF NOT EXISTS action_trace (
            seq         INTEGER PRIMARY KEY AUTOINCREMENT,
            request_id  TEXT NOT NULL,
            stage       TEXT NOT NULL,
            outcome     TEXT NOT NULL,
            intent      TEXT NOT NULL,
            arguments   TEXT NOT NULL,
            approver    TEXT NULL,
            result      TEXT NULL,
            external_id TEXT NULL,
            at          TEXT NOT NULL
        )
        """,
        "CREATE INDEX IF NOT EXISTS ix_action_trace_request ON action_trace(request_id)",
    ];

    /// <summary>启动时/构造存储时确保表存在。DDL 幂等，重复调用无副作用。</summary>
    public static void Ensure(IDbConnectionFactory connections)
    {
        using var connection = connections.Create();
        foreach (var statement in Statements)
            connection.Execute(statement);
    }
}
