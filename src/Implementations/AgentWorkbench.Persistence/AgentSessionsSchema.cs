namespace AgentWorkbench.Persistence;

/// <summary>
/// 会话快照表 DDL。<b>单一事实源</b>：M023 迁移与测试建库共用这一份，避免两处漂移。
/// </summary>
/// <remarks>
/// <c>state_json</c> 是 MAF 的序列化结果（含聊天历史），本表<b>不解释</b>它的结构——
/// 一旦这里开始读里面的字段，会话状态就与存储耦合，换 MAF 版本要改库。
/// <para>
/// <c>expires_at</c> 为空表示不过期；过期行由读路径与后台清理共同回收。
/// 这里之所以只在 <c>expires_at</c> 上建索引而不做定期任务：清理是「顺手」而非「必须」，
/// 漏掉一次不影响正确性，下一次读或下一次启动会补上。
/// </para>
/// </remarks>
public static class AgentSessionsSchema
{
    public const string TableName = "agent_sessions";

    public static readonly IReadOnlyList<string> Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS agent_sessions (
            session_id TEXT PRIMARY KEY NOT NULL,
            state_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            expires_at TEXT NULL
        )
        """,
        // 清理按 expires_at 扫；不带这个索引会退化成全表扫描。
        "CREATE INDEX IF NOT EXISTS ix_agent_sessions_expires ON agent_sessions(expires_at)",
    ];
}
