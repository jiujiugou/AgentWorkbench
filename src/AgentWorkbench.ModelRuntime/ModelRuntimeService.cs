using Microsoft.Extensions.Options;

namespace AgentWorkbench.ModelRuntime;

/// <inheritdoc />
/// <remarks>
/// 无状态门面：持有策略与用量累计器，<c>Wrap</c> 时创建新的装饰客户端，真正的逻辑在 <see cref="ModelRuntimeChatClient"/>。
/// 注册为单例（见 <c>ModelRuntimeServiceCollectionExtensions.AddNitroModelRuntime</c>），
/// 因此 <see cref="ModelUsageMeter"/> 的计数在全进程内共享。
/// </remarks>
public sealed class ModelRuntimeService : IModelRuntime
{
    // 策略参数（构造时快照，之后不变）。
    private readonly ModelRuntimeOptions _options;
    // 用量累计器（单例传入，跨实例共享）。
    private readonly ModelUsageMeter _meter;
    // 用于按类别派生装饰客户端的日志器（使日志类别归到 ModelRuntimeChatClient）。
    private readonly ILoggerFactory _loggerFactory;
    // 本门面自身的日志器。
    private readonly ILogger<ModelRuntimeService> _logger;

    /// <summary>构造函数。</summary>
    /// <param name="options">策略参数（单例）。</param>
    /// <param name="meter">用量累计器（单例）。</param>
    /// <param name="loggerFactory">日志器工厂，用于为装饰客户端派生日志器。</param>
    /// <param name="logger">本门面的日志器。</param>
    public ModelRuntimeService(
        IOptions<ModelRuntimeOptions> options,
        ModelUsageMeter meter,
        ILoggerFactory loggerFactory,
        ILogger<ModelRuntimeService> logger)
    {
        _options = options.Value;
        _meter = meter;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 幂等实现方式：靠类型检查（<c>is ModelRuntimeChatClient</c>）判断是否已包装，命中即原样返回，
    /// 避免同一客户端被叠上多套超时 / 重试 / 闸门。
    /// </remarks>
    public IChatClient Wrap(IChatClient innerClient)
    {
        // 已包装过就不再叠加：幂等保护，防止重复超时与重复重试。
        if (innerClient is ModelRuntimeChatClient)
            return innerClient;

        // 记录本层的生效策略，便于对照「实际调用」与「预期配置」。
        _logger.LogDebug(
            "Wrapping chat client with ModelRuntime (timeout={TimeoutSeconds}s retries={MaxRetries} maxConcurrency={MaxConcurrency})",
            _options.RequestTimeoutSeconds, _options.MaxRetries, _options.MaxConcurrentRequests);
        // 每次 Wrap 都新建装饰实例（幂等分支已挡住重复包装），日志类别为 ModelRuntimeChatClient。
        return new ModelRuntimeChatClient(
            innerClient, _options, _meter, _loggerFactory.CreateLogger<ModelRuntimeChatClient>());
    }

    /// <inheritdoc />
    public ModelUsageSnapshot GetUsage() => _meter.Snapshot();
}
