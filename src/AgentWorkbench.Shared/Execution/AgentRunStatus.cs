namespace AgentWorkbench.Shared;

/// <summary>
/// 一次 Agent run 的终态（落 <c>agent_runs.status</c>）。
/// </summary>
/// <remarks>
/// 取值长度受历史列宽约束（M014 <c>status</c> = 16），新增取值必须短于等于 16 个字符。
/// 状态回答「以什么状态结束的」，<see cref="AgentRunCode"/> 回答「为什么」——「跑满迭代被强制收口」
/// 与「答完了」是两种收尾，「用户取消」也不是失败，都必须能分开。
/// </remarks>
public static class AgentRunStatus
{
    /// <summary>模型正常收尾（没有触发任何预算）。</summary>
    public const string Succeeded = "succeeded";

    /// <summary>运行失败（具体归类见 error_code）。</summary>
    public const string Failed = "failed";

    /// <summary>超时：整轮超出 run 预算，或单次模型请求超时。</summary>
    public const string Timeout = "timeout";

    /// <summary>调用方取消。</summary>
    public const string Cancelled = "cancelled";

    /// <summary>跑满迭代预算、被框架强制收口（不是「答完了」）。</summary>
    public const string IterationLimit = "iteration_limit";

    /// <summary>token 预算耗尽。</summary>
    public const string BudgetExceeded = "budget_exceeded";

    /// <summary>模型重复调用同一工具（参数相同）到上限，判定为循环。</summary>
    public const string LoopDetected = "loop_detected";
}
