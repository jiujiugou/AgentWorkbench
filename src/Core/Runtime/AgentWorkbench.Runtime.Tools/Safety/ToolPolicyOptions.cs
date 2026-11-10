namespace AgentWorkbench.Runtime.Tools;

/// <summary>
/// 工具授权配置：白名单内的工具才允许注册与调用（不在其中 = 模型看不到、也调不动）。
/// 配置路径沿用 <c>Harness:Tools:Whitelist</c>，与既有 appsettings 兼容。
/// </summary>
public sealed class ToolPolicyOptions
{
    public const string SectionName = "Harness:Tools";

    /// <summary>
    /// 工具白名单（不在其中不注册、不可调用）。
    /// 检索走 MCP：<c>search_knowledge</c> 这个内置工具已删除，由 <c>mcp__rag__*</c> 承担。
    /// </summary>
    public List<string> Whitelist { get; set; } =
    [
        "get_current_time",
        "calculator",
        "mcp__rag__*",
        "list_documents",
        "remember",
        "recall"
    ];
}
