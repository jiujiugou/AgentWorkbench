using System.Diagnostics;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace AgentWorkbench.Knowledge.Local.Embedding;

/// <summary>
/// Embedding 的可靠性装饰器：管超时、重试与失败归因，不碰业务决策。
/// </summary>
/// <remarks>
/// <para><b>职责边界</b>：本类只回答"尽力而为之后为什么失败"；"这条业务链要不要继续"由调用边界决定
/// （索引写入失败要让文档转 failed，检索失败要降级为纯全文——同一失败两种处置，装饰器无权代劳）。</para>
/// <para><b>重试只针对暂时性失败</b>：<see cref="EmbeddingFailure.IsTransient"/> 决定是否重试。
/// 401/404/维度不符这类永久失败直接冒泡，不浪费时间也不掩盖配置错误。</para>
/// <para><b>日志级别下沉</b>：这里只打 Debug；最终 Warning 由持有 documentId 的调用边界记录，
/// 否则同一失败会在两层各打一条，日志噪音掩盖真问题。</para>
/// <para><b>为什么不返回 Result 类型</b>：<see cref="IEmbeddingProvider"/> 的调用方都在 catch 上，
/// 换成 Result 要同时改三层签名；失败原因由 <see cref="EmbeddingUnavailableException.Failure"/> 携带，
/// 信息不丢。</para>
/// </remarks>
public sealed class ResilientEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>
    /// 内部预算系数：用于"尝试 + 退避"的部分占总预算的比例，余量留给外层总超时，
    /// 避免最后一次尝试或退避恰好被总预算截断（重试白跑）。
    /// </summary>
    private const double InternalBudgetFraction = 0.8;

    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(300);

    private readonly IEmbeddingProvider _inner;
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger _logger;
    private readonly int _expectedDimensions;
    private readonly int _maxRetryAttempts;

    private volatile EmbeddingFailure? _lastFailure;
    private long _failureCount;

    /// <param name="inner">具体实现（ollama / OpenAI 兼容端点）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="options">Embedding 配置：维度、总预算、重试次数。</param>
    /// <param name="retryDelay">首次退避延迟；null 时 300ms。测试传零延迟以免真等。</param>
    public ResilientEmbeddingProvider(
        IEmbeddingProvider inner,
        ILogger<ResilientEmbeddingProvider> logger,
        EmbeddingOptions options,
        TimeSpan? retryDelay = null)
    {
        _inner = inner;
        _logger = logger;
        _expectedDimensions = options.Dimensions;

        var totalBudget = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
        var attempts = Math.Max(0, options.MaxRetryAttempts);
        var firstDelay = retryDelay ?? DefaultRetryDelay;
        _maxRetryAttempts = attempts;

        // 每次尝试超时与退避预算均从总预算内派生，保证 (尝试 + 退避) 之和不超过总预算，
        // 从而让重试次数真正生效；无重试则单次尝试吃满总预算。
        TimeSpan? attemptTimeout = null;
        if (attempts > 0)
        {
            var usableTicks = Math.Max(1, (long)(totalBudget.Ticks * InternalBudgetFraction));
            var backoffBudgetTicks = usableTicks / 5;
            var attemptTicks = Math.Max(1, (usableTicks - backoffBudgetTicks) / (attempts + 1));
            attemptTimeout = TimeSpan.FromTicks(attemptTicks);

            // 指数退避 (D,2D,4D…) 之和 = D·(2^attempts−1)，令其不超过退避预算。
            var expSum = Math.Pow(2, attempts) - 1;
            var fitDelayMs = backoffBudgetTicks * 1000.0 / TimeSpan.TicksPerMillisecond / expSum;
            firstDelay = TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(firstDelay.TotalMilliseconds, fitDelayMs)));
        }

        var builder = new ResiliencePipelineBuilder()
            .AddTimeout(totalBudget); // 外层：整轮总预算（硬上限）

        if (attempts > 0)
        {
            builder = builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = attempts,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = firstDelay,
                    MaxDelay = attemptTimeout!.Value,
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(
                        ex => EmbeddingFailure.Classify(ex).IsTransient)
                })
                .AddTimeout(attemptTimeout!.Value); // 内层：每次尝试超时（退避不计入）
        }

        _pipeline = builder.Build();
    }

    public string Name => _inner.Name;

    public string Model => _inner.Model;

    /// <summary>透传内层；"已配置"不代表服务可用——真实可用性看 <see cref="LastFailure"/>。</summary>
    public bool IsConfigured => _inner.IsConfigured;

    public int Dimensions => _expectedDimensions;

    /// <summary>最近一次失败归因；成功一次后仍保留，用于诊断"上次为什么没建成"。</summary>
    public EmbeddingFailure? LastFailure => _lastFailure;

    /// <summary>累计失败轮数（重试耗尽才算一轮）。</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
            return [];

        var sw = Stopwatch.StartNew();
        try
        {
            var vectors = await _pipeline.ExecuteAsync(
                async token => await _inner.EmbedAsync(texts, token),
                cancellationToken);

            // 契约校验：上游"成功但形状不对"比抛异常更危险——写进向量库会长期污染检索结果。
            Validate(vectors, texts.Count);

            _logger.LogDebug(
                "EmbedAsync completed: Model={Model} Count={Count} Dimensions={Dims} ElapsedMs={ElapsedMs}",
                Model, texts.Count, _expectedDimensions, sw.ElapsedMilliseconds);
            return vectors;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消不是失败：原样冒泡，不记失败、不包装。
            throw;
        }
        catch (EmbeddingUnavailableException)
        {
            // Validate 抛出的契约破坏，已带归因。
            throw;
        }
        catch (Exception ex)
        {
            var failure = EmbeddingFailure.Classify(ex);
            if (failure.Kind == FailureKind.NotFailure)
                throw;

            Record(failure);
            _logger.LogDebug(
                "EmbedAsync failed: Model={Model} Code={Code} Kind={Kind} Retried={Retries} ElapsedMs={ElapsedMs} Detail={Detail}",
                Model, failure.Code, failure.Kind, _maxRetryAttempts, sw.ElapsedMilliseconds, failure.Message);
            throw new EmbeddingUnavailableException(failure, ex);
        }
    }

    /// <summary>
    /// 校验返回形状。维度不符必须显式失败：sqlite-vec 的 <c>FLOAT[1024]</c> 是硬约束，
    /// 静默写入会让后续所有相似度计算失去意义。
    /// </summary>
    private void Validate(float[][] vectors, int expectedCount)
    {
        if (vectors.Length != expectedCount)
            throw new EmbeddingUnavailableException(
                new EmbeddingFailure(
                    EmbeddingFailureCode.CountMismatch,
                    FailureKind.Permanent,
                    $"返回 {vectors.Length} 条，期望 {expectedCount} 条"),
                new InvalidOperationException("Embedding 返回数量不匹配"));

        for (var i = 0; i < vectors.Length; i++)
        {
            if (vectors[i].Length != _expectedDimensions)
                throw new EmbeddingUnavailableException(
                    new EmbeddingFailure(
                        EmbeddingFailureCode.DimensionMismatch,
                        FailureKind.Permanent,
                        $"第 {i} 条向量 {vectors[i].Length} 维，配置期望 {_expectedDimensions} 维"),
                    new InvalidOperationException("Embedding 返回维度不匹配"));
        }
    }

    private void Record(EmbeddingFailure failure)
    {
        _lastFailure = failure;
        Interlocked.Increment(ref _failureCount);
    }
}
