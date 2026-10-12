namespace AgentWorkbench.Shared;

/// <summary>累计模型用量（请求数 + 输入 / 输出 token）。只如实累计，不判定阈值。</summary>
public readonly record struct AgentUsage(long Requests, long InputTokens, long OutputTokens)
{
    /// <summary>预算口径：输入 + 输出 token。</summary>
    public long Tokens => InputTokens + OutputTokens;
}

/// <summary>
/// 用量读取口：执行控制要判 token 预算，但不能依赖模型运行时，由编排层把它接过来。
/// </summary>
public interface IAgentUsageSource
{
    /// <summary>读取当前累计用量快照。</summary>
    AgentUsage GetUsage();
}
