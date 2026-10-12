namespace AgentWorkbench.AgentLoop;

/// <summary>
/// 执行控制守卫（一次 run 一个实例，状态随 run 增长）：管住「卡在同一个调用上」与「钱」。
/// </summary>
/// <remarks>
/// 环内信息只有到工具调用发出时才有，所以判定点放在 <see cref="InspectToolCall"/>：
/// <list type="bullet">
/// <item>重复调用：连续出现「同工具 + 同参数」达到 <see cref="AgentLoopOptions.MaxRepeatedToolCalls"/> 次即终止。</item>
/// <item>token 预算：把用量来源的读数减去本 run 起点快照，超过 <see cref="AgentLoopOptions.MaxTokensPerRun"/> 即终止。</item>
/// </list>
/// 收尾分类放在 <see cref="InspectCompletion"/>：框架跑满 <c>MaximumIterationsPerRequest</c> 后不会报错，
/// 而是清空工具、逼模型用文本收口再正常返回；若最后一次工具调用的轮次已经顶到预算，
/// 说明这轮是「被截断」而不是「答完了」。
/// </remarks>
public sealed class AgentRunGuard : IAgentRunGuard
{
    private readonly AgentLoopOptions _options;
    private readonly IAgentUsageSource? _usage;
    private readonly AgentUsage _baseline;

    private string? _lastSignature;
    private int _repeats;
    private int _lastIteration = -1;

    /// <param name="options">执行控制策略（预算与阈值）。</param>
    /// <param name="usage">用量来源；为 <c>null</c> 时不判 token 预算。构造时取一次基线快照，作为本 run 的用量起点。</param>
    public AgentRunGuard(AgentLoopOptions options, IAgentUsageSource? usage = null)
    {
        _options = options;
        _usage = usage;
        _baseline = usage?.GetUsage() ?? default;
    }

    /// <inheritdoc />
    public AgentRunStop? InspectToolCall(ToolCallObservation call)
    {
        if (call.Iteration > _lastIteration)
            _lastIteration = call.Iteration;

        return DetectLoop(call) ?? DetectBudgetOverage();
    }

    /// <inheritdoc />
    public AgentRunStop? InspectCompletion()
    {
        // 一次工具调用都没发生：模型直接答完，谈不上「跑满」。
        if (_lastIteration < 0 || _options.MaxIterations <= 0)
            return null;

        // 工具轮次 0 起：最后一个允许的轮次是 MaxIterations - 1。模型在这一轮还要调工具，
        // 说明它想要更多轮，而框架下一轮已清空工具、只能收口 —— 这就是「预算跑满」。
        if (_lastIteration < _options.MaxIterations - 1)
            return null;

        return new AgentRunStop(AgentRunStatus.IterationLimit, AgentRunCode.IterationLimit,
            $"已跑满迭代预算（{_options.MaxIterations} 轮工具调用），模型被强制收口");
    }

    private AgentRunStop? DetectLoop(ToolCallObservation call)
    {
        if (_options.MaxRepeatedToolCalls <= 0)
            return null;

        // 指纹 = 工具名 + 参数文本（参数文本键序稳定，见 ToolExecutionPipeline）。
        var signature = $"{call.Name}\u0000{call.Arguments}";
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
            _repeats++;
        else
        {
            _lastSignature = signature;
            _repeats = 1;
        }

        if (_repeats < _options.MaxRepeatedToolCalls)
            return null;

        return new AgentRunStop(AgentRunStatus.LoopDetected, AgentRunCode.LoopDetected,
            $"模型连续 {_repeats} 次以相同参数重复调用工具 {call.Name}，判定为循环，已终止");
    }

    private AgentRunStop? DetectBudgetOverage()
    {
        if (_options.MaxTokensPerRun <= 0 || _usage is null)
            return null;

        var used = _usage.GetUsage().Tokens - _baseline.Tokens;
        if (used <= _options.MaxTokensPerRun)
            return null;

        return new AgentRunStop(AgentRunStatus.BudgetExceeded, AgentRunCode.BudgetExceeded,
            $"token 预算耗尽（已用 {used}，上限 {_options.MaxTokensPerRun}），已终止");
    }
}
