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

    /// <summary>
    /// 这条消息由哪一次 run 产生。**只有 assistant 消息有值**，user 消息与历史数据为 null。
    ///
    /// 存在的理由只有一个：界面回看历史时要能顺这个 id 找回「当时的执行过程」
    /// （工具调用、迭代数、终态）。没有它，历史对话里的过程面板永远是空的——
    /// 而那是「模型说它做了什么」和「系统真的做了什么」唯一的对照物。
    /// </summary>
    public long? RunId { get; set; }
}
