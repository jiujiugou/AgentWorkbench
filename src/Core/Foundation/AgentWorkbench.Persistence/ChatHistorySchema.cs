namespace AgentWorkbench.Persistence;

/// <summary>
/// 会话历史表 DDL。<b>单一事实源</b>：M031 迁移与存储构造共用这一份，避免两处漂移。
///
/// 为什么是「一行一条消息」而不是「一行一份会话快照」：
/// 快照是整份覆盖写，两个 run 并发推进同一会话时，后写的会把先写的整段盖掉（lost update），
/// 模型下一轮读不到自己刚办过的事，会告诉用户「没办过」，用户据此重复下单。
/// 逐条追加没有「覆盖」这个动作，因此不需要任何锁来保证不丢。
///
/// 刻意<b>不</b>建 (session_id, message_id) 唯一约束：消息没有稳定的业务主键，
/// 按内容派生 id 会把「用户连说两遍同一句话」误当重复合并，
/// 那是两次合法输入，必须各留一条。幂等由动作层（request_id）负责，不在这里伪装。
/// </summary>
public static class ChatHistorySchema
{
    public const string TableName = "chat_messages";

    public static readonly IReadOnlyList<string> Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS chat_messages (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            session_id TEXT NOT NULL,
            role       TEXT NOT NULL,
            payload    TEXT NOT NULL,
            created_at TEXT NOT NULL
        )
        """,
        // 按 (session_id, id) 覆盖读取：id 全局递增，会话内即天然时序。
        "CREATE INDEX IF NOT EXISTS ix_chat_messages_session ON chat_messages(session_id, id)",
    ];
}
