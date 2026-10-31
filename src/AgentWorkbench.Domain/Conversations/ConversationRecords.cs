namespace AgentWorkbench.Domain;

/// <summary>对话会话（通用聊天上下文持久化）。</summary>
public sealed class ConversationRecord
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>所属租户；阶段 0.5 起用于会话隔离，历史数据为 null。</summary>
    public string? TenantId { get; set; }

    /// <summary>所属用户；与 <see cref="TenantId"/> 一起决定谁能看见这条会话。</summary>
    public string? UserId { get; set; }
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
