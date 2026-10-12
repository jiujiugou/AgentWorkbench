namespace AgentWorkbench.Shared;

/// <summary>
/// 一次 Agent run 的失败 / 终止归类码（落 <c>agent_runs.error_code</c>）。
/// </summary>
/// <remarks>
/// 只有一串人类可读的消息时，调用方无法据此决定「重试、降级还是提示用户」，所以每一类都有独立码。
/// 与 <see cref="AgentRunStatus"/> 的分工：状态说「以什么状态结束」，本码说「为什么」——
/// 例如同为 <see cref="AgentRunStatus.Timeout"/>，<see cref="RunTimeout"/> 与 <see cref="ModelTimeout"/>
/// 的处理方式不同；同为 <see cref="AgentRunStatus.Failed"/>，<see cref="ModelUnavailable"/> 与
/// <see cref="ToolError"/> 的重试价值也不同。
/// </remarks>
public static class AgentRunCode
{
    /// <summary>重复调用到上限，判定为循环。</summary>
    public const string LoopDetected = "loop_detected";

    /// <summary>token 预算耗尽。</summary>
    public const string BudgetExceeded = "budget_exceeded";

    /// <summary>跑满迭代预算被强制收口。</summary>
    public const string IterationLimit = "iteration_limit";

    /// <summary>整轮超出 run 级墙钟预算（AgentLoop 施加）。</summary>
    public const string RunTimeout = "run_timeout";

    /// <summary>单次模型请求超时（ModelRuntime 施加）。</summary>
    public const string ModelTimeout = "model_timeout";

    /// <summary>调用方取消。</summary>
    public const string Cancelled = "cancelled";

    /// <summary>没有可用模型（未配置 / 解析不到端点）。</summary>
    public const string ModelUnavailable = "model_unavailable";

    /// <summary>工具抛异常且未被容忍（连续失败越限，异常冒泡出执行环）。</summary>
    public const string ToolError = "tool_error";

    /// <summary>其余未归类异常。</summary>
    public const string Unhandled = "unhandled_error";
}
