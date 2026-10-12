using AgentWorkbench.ModelRuntime;
using AgentWorkbench.Shared;

namespace AgentWorkbench.AgentHarness;

/// <summary>
/// 把模型运行时的用量计数器接到执行控制守卫上：AgentLoop 要判 token 预算，但不能依赖 ModelRuntime，
/// 所以由编排层（本层同时依赖两者）把读数转成 <see cref="IAgentUsageSource"/>。
/// </summary>
/// <remarks>
/// <see cref="ModelUsageMeter"/> 是进程级单例，所有 run 共享同一份累计值。守卫在构造时取一次基线、
/// 之后读「当前值 − 基线」，这让单次 run 的用量口径正确，但**并发跑多个 run 时基线会互相干扰**
/// （A 的消耗会算进 B 的预算）。底座当前按「同一进程内顺序执行 run」使用；需要并发限额时，
/// 应改用按 run 独立的用量口径（例如作用域内的计量器）而不是进程级单例。
/// </remarks>
internal sealed class RuntimeUsageSource(IModelRuntime runtime) : IAgentUsageSource
{
    /// <inheritdoc />
    public AgentUsage GetUsage()
    {
        var snapshot = runtime.GetUsage();
        return new AgentUsage(snapshot.Requests, snapshot.InputTokens, snapshot.OutputTokens);
    }
}
