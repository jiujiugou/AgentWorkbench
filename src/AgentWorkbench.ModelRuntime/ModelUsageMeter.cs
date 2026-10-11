using System.Threading;

namespace AgentWorkbench.ModelRuntime;

/// <summary>累计的模型用量快照。Requests 为实际发出的请求数（重试的每次尝试各计一次）。</summary>
/// <param name="Requests">已发出的模型请求数。</param>
/// <param name="InputTokens">累计输入 token（未上报用量的调用不计入）。</param>
/// <param name="OutputTokens">累计输出 token（未上报用量的调用不计入）。</param>
public sealed record ModelUsageSnapshot(long Requests, long InputTokens, long OutputTokens);

/// <summary>进程内模型用量累计器（按调用次数 + 输入/输出 token 计数）。</summary>
/// <remarks>
/// 注册为单例，被所有装饰客户端实例共享；只负责「如实累计」，不判定阈值、不触发告警
/// （消费方是 Observability / 健康检查）。所有计数走 <see cref="Interlocked"/>，可安全并发更新。
/// </remarks>
public sealed class ModelUsageMeter
{
    private long _requests;
    private long _inputTokens;
    private long _outputTokens;

    /// <summary>记录一次已发出的模型请求（重试的每次尝试各计一次，无论成败）。</summary>
    public void RecordRequest() => Interlocked.Increment(ref _requests);

    /// <summary>记录一次响应上报的 token 用量；未上报用量的调用只计请求数。</summary>
    /// <param name="usage">响应携带的用量；为 <c>null</c> 或字段缺失时对应计数保持不变。</param>
    public void RecordUsage(UsageDetails? usage)
    {
        // 逐项判空累加：供应商可能只回传其中一项，甚至完全不回传（此时仅保留请求数）。
        if (usage?.InputTokenCount is { } input)
            Interlocked.Add(ref _inputTokens, input);
        if (usage?.OutputTokenCount is { } output)
            Interlocked.Add(ref _outputTokens, output);
    }

    /// <summary>读取当前累计值的原子快照。</summary>
    public ModelUsageSnapshot Snapshot()
        => new(Interlocked.Read(ref _requests), Interlocked.Read(ref _inputTokens), Interlocked.Read(ref _outputTokens));
}
