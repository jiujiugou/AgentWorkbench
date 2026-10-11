namespace AgentWorkbench.ModelRuntime;

/// <summary>
/// 模型运行时配置（配置节 <c>Harness:ModelRuntime</c>）。
/// 这些策略与供应商无关：无论底层接的是哪家 OpenAI 兼容端点，都走同一套超时 / 重试 / 并发闸门。
/// </summary>
/// <remarks>
/// 未配置该节时全部取下列默认值，即「不限单次超时、重试 2 次、退避基数 200ms、不限流」
/// —— 注意此时单次请求超时实际处于关闭状态（详见 README「现状与偏差」）。
/// 实例注册为单例，运行时策略在整个进程内保持一致。
/// </remarks>
public sealed class ModelRuntimeOptions
{
    /// <summary>配置节路径。</summary>
    public const string SectionName = "Harness:ModelRuntime";

    /// <summary>单次请求超时（秒）。<c>0</c> = 不限制（沿用调用方自己的取消令牌）。</summary>
    public int RequestTimeoutSeconds { get; set; }

    /// <summary>可重试的最大次数（仅针对连接类错误与 408/429/5xx）。<c>0</c> = 不重试。</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>第 n 次重试前的等待基数（毫秒）；实际等待 = 基数 × 2^(n-1)。</summary>
    public int RetryBaseDelayMilliseconds { get; set; } = 200;

    /// <summary>同时在途的模型请求上限（含流式请求的整个生命周期）。<c>0</c> = 不限制。</summary>
    public int MaxConcurrentRequests { get; set; }
}
