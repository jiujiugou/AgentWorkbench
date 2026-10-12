namespace AgentWorkbench.Memory;

/// <summary>
/// 记忆配置。配置路径沿用 <c>Harness:Memory</c>，与既有 appsettings 兼容。
/// </summary>
public sealed class MemoryOptions
{
    public const string SectionName = "Harness:Memory";

    /// <summary>是否维持会话内记忆（同一 sessionId 多轮历史）。</summary>
    public bool SessionEnabled { get; set; } = true;

    /// <summary>会话空闲过期时间（分钟）。<c>0</c> = 不过期。</summary>
    public int SessionTtlMinutes { get; set; } = 1440;

    /// <summary>进程内会话缓存条数上限。<c>0</c> = 不限制。</summary>
    public int MaxCachedSessions { get; set; } = 1000;

    /// <summary>落库会话的过期清理周期（秒），由 <c>SessionRetentionService</c> 使用。</summary>
    public int EvictionSweepSeconds { get; set; } = 60;
}
