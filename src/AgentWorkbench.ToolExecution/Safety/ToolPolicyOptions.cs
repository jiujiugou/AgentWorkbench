namespace AgentWorkbench.ToolExecution;

/// <summary>
/// 工具授权配置：白名单内的工具才允许注册与调用（不在其中 = 模型看不到、也调不动）。
/// 配置路径沿用 <c>Harness:Tools:Whitelist</c>，与既有 appsettings 兼容。
/// </summary>
public sealed class ToolPolicyOptions
{
    public const string SectionName = "Harness:Tools";

    /// <summary>工具白名单（不在其中不注册、不可调用）。</summary>
    public List<string> Whitelist { get; set; } =
    [
        "get_current_time",
        "calculator",
        "search_knowledge",
        "list_documents",
        "remember",
        "recall"
    ];
}
