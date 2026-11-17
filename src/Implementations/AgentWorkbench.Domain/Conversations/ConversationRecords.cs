namespace AgentWorkbench.Domain;

/// <summary>对话会话（通用聊天上下文持久化）。</summary>
/// <remarks>
/// 一条会话下挂多条 <see cref="MessageRecord"/>（conversation_id 外键，级联删除）。
/// 归属由 <see cref="TenantId"/> + <see cref="UserId"/> 决定，仓储的每个查询都强制带这两个条件——
/// 实体本身不判归属，它是被判的对象。
/// </remarks>
public sealed class ConversationRecord
{
    /// <summary>
    /// 会话 id：<c>Guid.NewGuid().ToString("N")</c>，即 32 位十六进制、无连字符。
    ///
    /// 格式不是随便挑的：迁移把主键列定为 <c>AsString(32)</c>，而
    /// <c>ConversationRepository.GetOrCreateAsync</c> 用 <c>id.Length == 32</c> 判断
    /// 「这像个合法会话 id」——长度不对就直接当没有，连查都不查。
    /// 改成带连字符的 36 位形式，新会话的 id 会过不了这道长度检查，刷新一次就变一个新会话。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 列表标题。创建时写定：调用方传了就 <c>Trim</c>，没传就是「新会话」。
    /// 当前没有后续更新路径——它不随首条消息自动生成，也不随消息变化。
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>创建时间，UTC 的 round-trip 格式（<c>DateTimeOffset.UtcNow.ToString("o")</c>），创建后不变。</summary>
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>
    /// 最后一条消息的时间；会话列表按它倒序。
    ///
    /// 与 <see cref="CreatedAt"/> 同时初始化，但此后只由 <c>AddMessageAsync</c> 推进，
    /// 且推进与消息插入共用同一个事务——只成功一条的话，列表排序会和实际最后一条消息脱节。
    /// 注意它不是「任意字段最后修改时间」：改标题不会动它。
    /// </summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>
    /// 所属租户；阶段 0.5（M025）起用于会话隔离，之前的历史行为 null。
    /// null 的行不匹配任何非空 tenantId 查询，即旧会话对所有人不可见——不是对所有人可见。
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// 所属用户；与 <see cref="TenantId"/> 一起决定谁能看见这条会话。
    /// 越权防护的实现处在仓储的 where 条件里，这里只存值、不判。
    /// </summary>
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
