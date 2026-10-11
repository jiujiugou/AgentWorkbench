namespace AgentWorkbench.Domain;

/// <summary>对话会话（通用聊天上下文持久化）。</summary>
public sealed class ConversationRecord
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

/// <summary>会话消息。</summary>
public sealed class MessageRecord
{
    public long Id { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}
