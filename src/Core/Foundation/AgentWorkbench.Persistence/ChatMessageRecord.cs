namespace AgentWorkbench.Persistence;

/// <summary>
/// <c>chat_messages</c> 的一行：一次 run 里追加进会话的一条消息。
///
/// 只映射，不建表——schema 的唯一真相是 FluentMigrator（`M031_ChatHistory`），
/// 与 <see cref="AppDbContext"/> 的其余实体一致。
/// </summary>
public sealed class ChatMessageRecord
{
    /// <summary>SQLite 自增主键，会话内的读回顺序就靠它（不用时间戳：同一批消息时间戳相同）。</summary>
    public long Id { get; set; }

    public string SessionId { get; set; } = string.Empty;

    /// <summary>冗余一列角色，便于直接查「这轮模型说了什么」而不用反序列化 payload。</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>单条 <c>ChatMessage</c> 的 JSON（含 functionCall / functionResult 的类型判别）。</summary>
    public string Payload { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;
}
