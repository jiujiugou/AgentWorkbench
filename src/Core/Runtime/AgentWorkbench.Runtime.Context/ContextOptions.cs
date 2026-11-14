namespace AgentWorkbench.Runtime.Context;

/// <summary>
/// 请求装配配置。配置路径沿用 <c>Harness:Context</c>，与既有 appsettings 兼容。
/// </summary>
/// <remarks>
/// 只有一个开关。历史条数归 MAF 的 <c>ChatHistoryProvider</c>（框架无统一条数开关），
/// 片段字符预算要等片段注入真正接进 run 时再加回来——现在加就是死配置。
/// </remarks>
public sealed class ContextOptions
{
    public const string SectionName = "Harness:Context";

    /// <summary>是否启用请求装配（指令注入）。关闭后 <c>ContextPlan.Instructions</c> 恒为 <c>null</c>。</summary>
    public bool Enabled { get; set; } = true;
}
