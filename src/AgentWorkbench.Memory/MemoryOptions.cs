namespace AgentWorkbench.Memory;

/// <summary>
/// 记忆配置。配置路径沿用 <c>Harness:Memory</c>，与既有 appsettings 兼容。
/// </summary>
public sealed class MemoryOptions
{
    public const string SectionName = "Harness:Memory";

    /// <summary>是否维持会话内记忆（同一 sessionId 多轮历史）。</summary>
    public bool SessionEnabled { get; set; } = true;
}
