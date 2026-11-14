namespace AgentWorkbench.Persistence;

/// <summary>
/// 长期记忆表 DDL。<b>单一事实源</b>：M024 迁移与测试建库共用这一份。
/// </summary>
/// <remarks>
/// 隔离维度是 <c>(scope_type, scope_id)</c>，覆盖维度是 <c>mem_key</c>：
/// 同 <c>(scope_type, scope_id, mem_key)</c> 的写入走 UPSERT 覆盖，<c>mem_key</c> 为 NULL 时总是新增——
/// SQLite 的唯一索引把多个 NULL 视为互不相同，所以「没有 key 的记忆」不会被去重键误伤。
/// <para>
/// <c>importance</c> 用 REAL 而不是 NUMERIC：NUMERIC 在 SQLite 里是「亲和类型」，
/// 0.5 会被原样存成 TEXT，读回来做 <c>CompareTo</c> 就变成字符串比较（"10" &lt; "9"）。
/// </para>
/// <para>
/// <c>use_count</c> / <c>last_used_at</c> 是「被召回了几次」，不参与打分排序的硬判定，
/// 只作为保留期清理的参考——不要把它当热度分读。
/// </para>
/// </remarks>
public static class MemoryItemsSchema
{
    public const string TableName = "memory_items";

    public static readonly IReadOnlyList<string> Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS memory_items (
            id                TEXT PRIMARY KEY NOT NULL,
            scope_type        TEXT NOT NULL,
            scope_id          TEXT NOT NULL,
            kind              TEXT NOT NULL,
            mem_key           TEXT NULL,
            content           TEXT NOT NULL,
            importance        REAL NOT NULL,
            source_session_id TEXT NULL,
            source_run_id     INTEGER NULL,
            created_at        TEXT NOT NULL,
            updated_at        TEXT NOT NULL,
            last_used_at      TEXT NULL,
            use_count         INTEGER NOT NULL DEFAULT 0,
            expires_at        TEXT NULL
        )
        """,
        // UPSERT 的冲突目标：这三个字段的组合决定「是新增还是覆盖」。
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_items_key ON memory_items(scope_type, scope_id, mem_key)",
        // 按作用域取记忆是唯一的热路径。
        "CREATE INDEX IF NOT EXISTS ix_memory_items_scope ON memory_items(scope_type, scope_id)",
        // 保留期清理按 expires_at 扫。
        "CREATE INDEX IF NOT EXISTS ix_memory_items_expires ON memory_items(expires_at)",
    ];
}
