namespace AgentWorkbench.AgentLoop;

/// <summary>
/// 执行控制：为一次 Agent 运行提供预算（最大迭代 / 总耗时）与「带超时执行」的包装。
/// 迭代循环本体由 Microsoft Agent Framework 的 FunctionInvokingChatClient 承担，
/// 本模块负责「允许循环多久、允许循环几次」，并在超时后抛出统一的 <see cref="TimeoutException"/>。
/// </summary>
public interface IAgentLoop
{
    /// <summary>工具环上限（传给执行层作为 MaximumIterations）。</summary>
    int MaxIterations { get; }

    /// <summary>单次 run 的总超时（秒）。</summary>
    int TimeoutSeconds { get; }

    /// <summary>
    /// 为一次 run 新建执行控制守卫（重复调用检测 / token 预算），交给工具执行环在环内调用。
    /// </summary>
    /// <param name="usage">用量来源；为 <c>null</c>（或配置的预算为 0）时不判 token 预算。</param>
    IAgentRunGuard CreateGuard(IAgentUsageSource? usage = null);

    /// <summary>在 run 级超时约束下执行 <paramref name="action"/>；超时抛 <see cref="AgentRunTimeoutException"/>。</summary>
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// run 级超时（区别于模型请求自身的超时）：调用方可以按它区分「是整轮跑太久」还是「单次请求太久」。
/// 继承 <see cref="TimeoutException"/>，因此 <c>catch (TimeoutException)</c> 仍然生效。
/// </summary>
public sealed class AgentRunTimeoutException(int timeoutSeconds)
    : TimeoutException($"Harness 运行超时（{timeoutSeconds}s）")
{
    public int TimeoutSeconds { get; } = timeoutSeconds;
}
