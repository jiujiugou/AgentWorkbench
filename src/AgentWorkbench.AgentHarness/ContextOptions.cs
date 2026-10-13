namespace AgentWorkbench.AgentHarness;

/// <summary>
/// 上下文配置。配置路径沿用 <c>Harness:Context</c>，与既有 appsettings 兼容。
/// </summary>
/// <remarks>
/// 原属独立的 <c>AgentWorkbench.Context</c> 项目。阶段 4 砍伐：该项目的组装器（<c>ContextBuilder</c>）
/// 自阶段 0/1 起已零 src 消费者（检索改由模型主动调 <c>search_knowledge</c> 工具，不再由 harness 暗做），
/// 只剩这个能力位配置仍被 <c>GetCapabilitiesAsync</c> 读取。项目边界删掉，配置并入本模块。
/// </remarks>
public sealed class ContextOptions
{
    public const string SectionName = "Harness:Context";

    /// <summary>是否启用检索增强上下文（RAG 注入）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>注入的检索片段数量。</summary>
    public int RagTopK { get; set; } = 5;
}
