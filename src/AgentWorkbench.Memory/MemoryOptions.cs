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

    /// <summary>落库会话的过期清理周期（秒），由 <c>MemoryRetentionService</c> 使用。</summary>
    public int EvictionSweepSeconds { get; set; } = 60;

    /// <summary>是否启用长期记忆（工具 <c>remember</c> / <c>recall</c>）。关掉时这两个工具不注册。</summary>
    public bool LongTermEnabled { get; set; } = true;

    /// <summary>
    /// 工具写入的记忆作用域：<c>session</c>（默认，跟着当前会话）或 <c>global</c>（跨会话）。
    /// 工具还可用参数把单条记忆显式写成 <c>global</c>；这里只定默认值。
    /// </summary>
    public string LongTermScope { get; set; } = MemoryScopeTypes.Session;

    /// <summary>单条记忆内容的最大字符数，超出直接拒绝（避免把整段对话塞进记忆表）。</summary>
    public int MaxMemoryChars { get; set; } = 2000;

    /// <summary>召回候选上限默认值（工具未显式传 limit 时使用）。</summary>
    public int RecallCandidateK { get; set; } = 20;

    /// <summary>长期记忆默认保留天数（隐私口径）。<c>0</c> = 不过期。</summary>
    public int LongTermRetentionDays { get; set; }
}
