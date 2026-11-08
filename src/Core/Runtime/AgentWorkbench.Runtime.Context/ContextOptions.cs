namespace AgentWorkbench.Runtime.Context;

/// <summary>
/// 上下文组装配置。配置路径沿用 <c>Harness:Context</c>，与既有 appsettings 兼容。
/// </summary>
public sealed class ContextOptions
{
    public const string SectionName = "Harness:Context";

    /// <summary>是否启用上下文增强（片段注入 + 指令注入）。关闭后只发用户本轮输入。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>保留的历史消息条数（取最近 N 条）；<c>&lt;= 0</c> 表示不截断。</summary>
    public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>注入片段的总字符上限；超出按顺序截断。<c>&lt;= 0</c> 表示不截断。</summary>
    public int MaxFragmentChars { get; set; } = 6000;
}
