using System.ClientModel;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;

namespace AgentWorkbench.ModelRuntime;

/// <summary>
/// 统一调用语义的装饰客户端：
/// 单次请求超时（可选）、连接类错误与 408/429/5xx 重试（指数退避）、在途请求闸门、用量统计。
/// 流式请求只在「尚未吐出任何增量」时重试，避免把半截回答重复一遍。
/// </summary>
/// <remarks>
/// 装饰位置：位于 Providers 产出的真实 <see cref="IChatClient"/> 之上、Conversation / AgentHarness 之下 ——
/// 它看到的是「统一后的 MEA 调用」，不关心底层是哪家供应商。
/// 与相邻装饰层的顺序：Providers 的 <c>DiagnosticsChatClient</c> 在最内层（紧贴 HTTP），本层在其之上；
/// 因此本层 catch 到的异常已带上供应商原始失败信息，重试判据看到的也是同一批异常。
/// 职责边界：本类只做「约束与度量」，不改写请求 / 响应内容，也不做端点选择（那是 Providers 的事）。
/// 生命周期：每次 <c>Wrap</c> 都 new 一个本类实例（见 <see cref="ModelRuntimeService.Wrap"/>），
/// 策略参数来自单例 <see cref="ModelRuntimeOptions"/>，用量汇总到共享的单例 <see cref="ModelUsageMeter"/>。
/// </remarks>
internal sealed class ModelRuntimeChatClient : DelegatingChatClient
{
    // 策略参数（单例共享，构造后只读）：超时 / 重试 / 并发上限。
    private readonly ModelRuntimeOptions _options;
    // 用量累计器（单例共享）：本层只「如实计数」，阈值判定与预算决策不在这里。
    private readonly ModelUsageMeter _meter;
    // 在途并发闸门；MaxConcurrentRequests <= 0 时为 null（不限流）。
    // 注意：这是「实例级」信号量，而 Wrap 目前是每次调用各创建一个实例，
    // 因此当前装配下它不会跨请求限流（详见 README「现状与偏差」）。
    private readonly SemaphoreSlim? _gate;
    // 日志器，由 ModelRuntimeService 经 ILoggerFactory 派生，日志类别即本类。
    private readonly ILogger _logger;

    /// <summary>
    /// 用统一的超时 / 重试 / 并发 / 用量策略装饰 <paramref name="innerClient"/>。
    /// </summary>
    /// <param name="innerClient">被装饰的真实客户端（Providers 产出的 OpenAI 兼容客户端）。</param>
    /// <param name="options">策略参数；<c>MaxConcurrentRequests</c> 在此决定是否创建闸门。</param>
    /// <param name="meter">用量累计器（跨实例共享）。</param>
    /// <param name="logger">日志器。</param>
    public ModelRuntimeChatClient(
        IChatClient innerClient, ModelRuntimeOptions options, ModelUsageMeter meter, ILogger logger)
        : base(innerClient)
    {
        _options = options;
        _meter = meter;
        // 只在配置了正数上限时才建闸门；0 表示不限流，用 null 让热路径少一层等待。
        _gate = options.MaxConcurrentRequests > 0 ? new SemaphoreSlim(options.MaxConcurrentRequests) : null;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 非流式路径：每次尝试都在「独立的超时预算」内完成，失败按可重试性决定是否退避重试。
    /// </remarks>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // 物化消息列表：重试会再次枚举 messages，若原参数是一次性可枚举对象，第二次尝试就会拿到空序列。
        // 已经是集合时直接复用，避免多余分配。
        var materialized = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();

        // attempt 从 0 起递增；循环只通过 return / throw 退出，没有自然结束条件。
        for (var attempt = 0; ; attempt++)
        {
            // 每次尝试单独计时：重试拿到全新的超时预算，而不是复用上一次已消耗的时间。
            using var timeout = CreateTimeoutSource(cancellationToken);
            var token = timeout?.Token ?? cancellationToken;

            // 取闸门许可；等待本身也受本次超时约束（token），避免排队排到超时还占着槽位。
            if (_gate is not null)
                await _gate.WaitAsync(token);
            try
            {
                // 先记请求数再发：每次「实际发出」的请求都计数，重试的各次尝试各计一次。
                _meter.RecordRequest();
                var response = await base.GetResponseAsync(materialized, options, token);
                // 响应带回的 token 用量累加进账（未上报时 RecordUsage 内部会自动跳过）。
                _meter.RecordUsage(response.Usage);
                _logger.LogInformation(
                    "Model request completed: InputTokens={InputTokens} OutputTokens={OutputTokens} Total={TotalTokens}",
                    response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.TotalTokenCount);
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 外部令牌没被取消却收到取消 → 只可能是本次请求超时（token 来自 CancelAfter）。
                // 转成 TimeoutException，与 AgentLoop 的 run 级超时（AgentRunTimeoutException）区分开。
                throw TimedOut();
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && IsTransient(ex))
            {
                // 仍有重试次数且属瞬时错误：退避后进入下一轮（finally 会先释放闸门）。
                await BackoffAsync(ex, attempt, cancellationToken);
            }
            finally
            {
                // 所有退出路径（成功 / 超时 / 重试 / 其它异常）都经过这里，保证许可一定归还。
                _gate?.Release();
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 流式路径：闸门覆盖整个流的生命周期（从建流到吐完）；只有在「一个增量都还没吐出」时才允许重试，
    /// 一旦开始 yield 就不再重试，否则消费方会看到重复的半截回答。
    /// </remarks>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 同非流式：物化消息，供可能的重试复用。
        var materialized = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        // 跨重试尝试累积的用量（UsageContent 可能出现在流的任意一帧）。
        var usage = new UsageDetails();

        for (var attempt = 0; ; attempt++)
        {
            // 每次尝试独立计时；流式下该超时作用于「单次 MoveNextAsync 的等待」，而非整个流的墙钟时长。
            using var timeout = CreateTimeoutSource(cancellationToken);
            var token = timeout?.Token ?? cancellationToken;
            // 本轮是否已吐出过增量：一旦为 true，本条流不再允许重试。
            var yielded = false;
            // 本轮是否因瞬时错误需要重试：为 true 时跳出内层循环、释放闸门后退避重来。
            var retry = false;

            if (_gate is not null)
                await _gate.WaitAsync(token);
            try
            {
                _meter.RecordRequest();
                await using var enumerator = base
                    .GetStreamingResponseAsync(materialized, options, token)
                    .GetAsyncEnumerator(token);

                while (true)
                {
                    bool hasNext;
                    ChatResponseUpdate update;
                    try
                    {
                        // 手动驱动迭代器：成功时把 update yield 出去；
                        // hasNext 为 false（流正常结束）时下一行即 break，不会使用 update。
                        hasNext = await enumerator.MoveNextAsync();
                        update = enumerator.Current;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // 同非流式：外部未取消却收到取消 = 本次等待超时 → 统一转 TimeoutException。
                        throw TimedOut();
                    }
                    catch (Exception ex) when (!yielded && attempt < _options.MaxRetries && IsTransient(ex))
                    {
                        // 仅当还没吐出任何增量且属瞬时错误时才重试（覆盖开流阶段的失败）。
                        retry = true;
                        break;
                    }

                    if (!hasNext)
                        break;

                    // 收集本帧携带的用量信息（多数帧不含用量，仅少数帧带 UsageContent）。
                    foreach (var content in update.Contents)
                    {
                        if (content is UsageContent usageContent)
                            usage.Add(usageContent.Details);
                    }

                    yielded = true;
                    yield return update;
                }
            }
            finally
            {
                // 无论正常收流、重试还是异常，都归还闸门许可。
                _gate?.Release();
            }

            if (!retry)
            {
                // 正常收流：只有确实吐过增量才记 token 用量（空流 / 未上报记为 0，仅保留请求数）。
                _meter.RecordUsage(yielded ? usage : null);
                yield break;
            }

            // 重试前退避；流式路径未把异常对象带出 catch 块，故这里 failure 传 null（日志仍记录退避与次数）。
            await BackoffAsync(null, attempt, cancellationToken);
        }
    }

    /// <summary>
    /// 构造本次尝试的超时源；<c>RequestTimeoutSeconds &lt;= 0</c> 时返回 <c>null</c>（不限时，直接用调用方令牌）。
    /// </summary>
    /// <remarks>
    /// 用 CreateLinkedTokenSource 串联调用方令牌，使「调用方取消」与「本模块超时」都能触发取消；
    /// 二者在 catch 里靠 <c>cancellationToken.IsCancellationRequested</c> 区分。
    /// </remarks>
    private CancellationTokenSource? CreateTimeoutSource(CancellationToken cancellationToken)
    {
        // 0 / 负数 = 不设超时：返回 null，调用点回退为原令牌，行为等同「不拦截」。
        if (_options.RequestTimeoutSeconds <= 0)
            return null;

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        return source;
    }

    /// <summary>按指数退避等待，然后由调用方的循环进入下一次尝试。</summary>
    /// <remarks>
    /// 第 attempt(从 0 起) 次重试前的等待 = RetryBaseDelayMilliseconds × 2^attempt
    /// （首次重试等 base，随后 2×、4×…）。
    /// 这里用<b>调用方原始令牌</b>取消等待，而不是本次尝试的超时令牌 —— 否则一个短超时会连退避都一起取消掉。
    /// </remarks>
    /// <param name="failure">触发重试的异常；流式路径不带异常对象，可传 <c>null</c>。</param>
    /// <param name="attempt">当前尝试序号（0 起）。</param>
    /// <param name="cancellationToken">调用方原始取消令牌，用于取消退避等待。</param>
    private async Task BackoffAsync(Exception? failure, int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(_options.RetryBaseDelayMilliseconds * Math.Pow(2, attempt));
        _logger.LogWarning(
            failure,
            "模型请求失败，{DelayMs}ms 后重试（第 {Attempt}/{MaxRetries} 次）",
            delay.TotalMilliseconds, attempt + 1, _options.MaxRetries);
        await Task.Delay(delay, cancellationToken);
    }

    /// <summary>把「本次尝试超时」转成统一的异常类型，消息里带上配置的超时秒数便于排查。</summary>
    private TimeoutException TimedOut()
        => new($"模型请求超时（{_options.RequestTimeoutSeconds}s）");

    /// <summary>
    /// 判断异常是否属于「重试可能成功」的瞬时错误；命中才重试，非瞬时错误一次都不重试。
    /// </summary>
    /// <remarks>
    /// 两类来源：
    /// 连接 / 传输类 —— <see cref="HttpRequestException"/>、<see cref="IOException"/>、<see cref="SocketException"/>
    /// （DNS 失败、连接被拒、读中断等）；
    /// 服务端限流 / 暂不可用 —— OpenAI SDK 抛的 <see cref="ClientResultException"/>，且状态码为 408 / 429 / 5xx。
    /// 需要留意：<see cref="ClientResultException"/> 属 System.ClientModel（OpenAI SDK 体系），
    /// 这层判断因此隐含「底层是 OpenAI 兼容客户端」的假设（另见 README「现状与偏差」）。
    /// </remarks>
    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException => true,
        IOException => true,
        SocketException => true,
        ClientResultException { Status: 408 or 429 or >= 500 } => true,
        _ => false,
    };
}
