using System.Collections.Concurrent;
using System.Diagnostics;
using System.ClientModel;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;

namespace AgentWorkbench.Providers;

/// <summary>
/// 解析出的对话端点：供应商 + 模型 + 端点参数。不可变值对象，同时是客户端缓存的输入。
/// </summary>
/// <param name="ProviderId">
/// 来源供应商的主键；<c>null</c> 表示这次走的是 <see cref="ProviderOptions"/> 配置回退，而非数据库里的供应商。
/// </param>
/// <param name="BaseUrl">
/// OpenAI 兼容端点根地址（通常以 <c>/v1</c> 结尾），会被直接用作 OpenAI SDK 的 Endpoint。
/// </param>
/// <param name="ApiKey">
/// 访问密钥；空字符串表示该端点不校验密钥（本地 / 自建部署常见），创建客户端时会用占位值补齐。
/// </param>
/// <param name="Model">本次实际使用的模型名 —— 已按回退链解析完成，不会再为空。</param>
public sealed record ChatEndpoint(long? ProviderId, string BaseUrl, string ApiKey, string Model);

/// <summary>
/// 运行时解析当前对话端点：优先数据库里激活的供应商 + 默认模型，回退到环境变量 / appsettings。
/// 客户端按 (BaseUrl|ApiKey|Model) 缓存，切换模型无需重启。
/// </summary>
/// <remarks>
/// 职责边界：本接口只回答「调哪家、哪个模型」。一次调用的超时 / 重试 / 并发 / 用量统计
/// 由 <c>AgentWorkbench.ModelRuntime</c> 承担，不在本模块范围内。
/// </remarks>
public interface IChatClientFactory
{
    /// <summary>
    /// 解析出本次要使用的端点。
    /// </summary>
    /// <param name="modelOverride">
    /// 调用方指定的模型名；为 <c>null</c> / 空白时，按「该供应商的默认模型 → 该供应商的第一个模型 → 配置节默认模型」依次回退。
    /// </param>
    /// <param name="cancellationToken">取消令牌，透传给仓储查询。</param>
    /// <returns>
    /// 解析出的端点；数据库与配置都不可用时返回 <c>null</c> —— 由调用方决定如何提示「模型未配置」，
    /// 「未配置」因此是一个可查询的正常状态，而不是异常。
    /// </returns>
    Task<ChatEndpoint?> ResolveAsync(string? modelOverride, CancellationToken cancellationToken);

    /// <summary>
    /// 取得端点对应的 <see cref="IChatClient"/>；同一端点在进程内复用同一个客户端实例。
    /// </summary>
    /// <param name="endpoint">由 <see cref="ResolveAsync"/> 解析出的端点。</param>
    IChatClient GetClient(ChatEndpoint endpoint);
}

/// <inheritdoc />
/// <remarks>
/// 注册为单例（见 <c>ProvidersServiceCollectionExtensions.AddNitroProviders</c>）；
/// 客户端缓存本身是静态字段，因此缓存与 DI 生命周期无关，全进程唯一。
/// </remarks>
public sealed class ChatClientFactory : IChatClientFactory
{
    /// <summary>
    /// 进程内客户端缓存，key = <c>BaseUrl|ApiKey|Model</c>。
    /// 客户端内部持有 SDK 的 HttpClient 与管道状态，复用比每次新建更省资源；
    /// 命中同一个 key 直接返回，因此切换模型（key 变化）无需重启进程。
    /// </summary>
    /// <remarks>
    /// 已知取舍：
    /// <list type="number">
    /// <item>从不淘汰：端点或密钥改一次就会留下一个新 key，进程存活期间旧条目常驻。条目数量级很小（供应商数 × 模型数），可以接受；
    /// 若将来需要回收，替换为带过期策略的实现即可。</item>
    /// <item>key 里含明文 ApiKey：只存在于内存，不落库、不写日志 —— 日志只输出 BaseUrl 与 Model。</item>
    /// <item>key 含 ApiKey 是有意为之：客户端的凭据在创建时固化，密钥变更必须换客户端，否则会继续用旧凭据发请求。</item>
    /// </list>
    /// </remarks>
    private static readonly ConcurrentDictionary<string, IChatClient> Clients = new();

    private readonly IProviderRepository _providers;
    private readonly ProviderOptions _fallback;
    private readonly ILogger<ChatClientFactory> _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="providers">数据库侧的供应商 / 模型仓储（第一优先级来源）。</param>
    /// <param name="fallback">数据库为空时的兜底配置（第二优先级来源）。</param>
    /// <param name="logger">日志。</param>
    public ChatClientFactory(IProviderRepository providers, IOptions<ProviderOptions> fallback, ILogger<ChatClientFactory> logger)
    {
        _providers = providers;
        _fallback = fallback.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ChatEndpoint?> ResolveAsync(string? modelOverride, CancellationToken cancellationToken)
    {
        // 结构化日志作用域：本次解析产生的所有日志都带 Operation=ResolveEndpoint，便于按操作过滤。
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["Operation"] = "ResolveEndpoint" });
        var sw = Stopwatch.StartNew();

        // 第一优先级：数据库中处于激活状态的供应商（用户在「设置」页选中的那条）。
        var active = await _providers.GetActiveProviderAsync();
        if (active is not null && !string.IsNullOrWhiteSpace(active.BaseUrl))
        {
            // 模型名回退链，逐级降级直到取到非空模型名：
            //   1) 调用方显式指定（modelOverride）
            //   2) 该供应商标记为「默认」的模型
            //   3) 该供应商下任意一个模型（列表首个，兜底避免「有供应商却无模型名」）
            //   4) 配置节里的默认模型
            var model = modelOverride;
            if (string.IsNullOrWhiteSpace(model))
                model = (await _providers.GetDefaultModelAsync(active.Id))?.Model;
            if (string.IsNullOrWhiteSpace(model))
                model = (await _providers.ListModelsAsync(active.Id)).FirstOrDefault()?.Model;
            if (string.IsNullOrWhiteSpace(model))
                model = _fallback.Model;

            // model! 非空断言：走到这里 model 至少已由 _fallback.Model 兜底，而该配置项带非空默认值。
            var endpoint = new ChatEndpoint(active.Id, active.BaseUrl, active.ApiKey, model!);
            _logger.LogInformation("Resolved endpoint from active provider {ProviderId} model {Model} in {ElapsedMs}ms", active.Id, model, sw.ElapsedMilliseconds);
            return endpoint;
        }

        // 第二优先级：配置回退（环境变量 / appsettings 的 Provider 节）。ProviderId 置 null，表示来源不是数据库。
        if (_fallback.IsConfigured)
        {
            var endpoint = new ChatEndpoint(null, _fallback.BaseUrl, _fallback.ApiKey, modelOverride ?? _fallback.Model);
            _logger.LogInformation("Resolved endpoint from fallback config model {Model} in {ElapsedMs}ms", endpoint.Model, sw.ElapsedMilliseconds);
            return endpoint;
        }

        // 两者都没有：返回 null 而不是抛异常，让「模型未配置」成为调用方可判定的状态（前端据此提示去设置页）。
        _logger.LogWarning("No active provider and fallback not configured");
        return null;
    }

    /// <inheritdoc />
    public IChatClient GetClient(ChatEndpoint endpoint)
    {
        // 缓存 key 由三元组构成：
        //   BaseUrl —— 决定请求打向哪家；
        //   ApiKey  —— 决定用哪把钥匙（客户端的凭据在构造时固化）；
        //   Model   —— 决定模型名（OpenAI SDK 把模型名绑定在客户端实例上）。
        var key = $"{endpoint.BaseUrl}|{endpoint.ApiKey}|{endpoint.Model}";
        return Clients.GetOrAdd(key, _ =>
        {
            _logger.LogInformation("Creating new IChatClient for {BaseUrl} model {Model}", endpoint.BaseUrl, endpoint.Model);

            // OpenAI SDK 要求凭据非空；本地 / 自建的 OpenAI 兼容端点通常不校验密钥，
            // 用占位串满足构造要求，避免「不需要密钥」的端点因为空密钥而无法建客户端。
            var credential = new ApiKeyCredential(
                string.IsNullOrWhiteSpace(endpoint.ApiKey) ? "not-required" : endpoint.ApiKey);

            // 关键点：用 Endpoint 覆盖把 SDK 指向任意 OpenAI 兼容 /v1 地址 —— 这就是「多供应商」的实现方式：
            // 不区分厂商，只要求对方讲 OpenAI 协议。若将来要接原生协议（如 Anthropic / Gemini native），
            // 落点是在这里新增一个 IChatClient 适配实现，上层代码无需改动。
            var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint.BaseUrl) };
            var openAi = new OpenAIClient(credential, clientOptions);
            var chatClient = openAi.GetChatClient(endpoint.Model).AsIChatClient();

            // 包一层诊断中间件：让服务端返回的非成功响应（含完整 body）在日志中可见，异常本身原样透出。
            // 注意层次：诊断层紧贴真实客户端，位于 ModelRuntime 装饰器之下，因此它看到的是供应商的原始失败。
            return new DiagnosticsChatClient(chatClient, _logger);
        });
    }
}
