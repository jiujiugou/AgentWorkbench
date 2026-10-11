namespace AgentWorkbench.Context;

/// <summary>
/// 上下文配置。配置路径沿用 <c>Harness:Context</c>，与既有 appsettings 兼容。
/// </summary>
public sealed class ContextOptions
{
    public const string SectionName = "Harness:Context";

    /// <summary>是否启用检索增强上下文（RAG 注入）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>注入的检索片段数量。</summary>
    public int RagTopK { get; set; } = 5;
}
