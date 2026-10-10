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

/// <summary>解析出的对话端点：供应商 + 模型 + 端点参数。</summary>
public sealed record ChatEndpoint(long? ProviderId, string BaseUrl, string ApiKey, string Model);

/// <summary>
/// 运行时解析当前对话端点：优先数据库里激活的供应商 + 默认模型，回退到环境变量 / appsettings。
/// 客户端按 (BaseUrl|ApiKey|Model) 缓存，切换模型无需重启。
/// </summary>
public interface IChatClientFactory
{
    Task<ChatEndpoint?> ResolveAsync(string? modelOverride, CancellationToken cancellationToken);

    IChatClient GetClient(ChatEndpoint endpoint);
}

/// <inheritdoc />
public sealed class ChatClientFactory : IChatClientFactory
{
    private static readonly ConcurrentDictionary<string, IChatClient> Clients = new();

    private readonly IProviderRepository _providers;
    private readonly ProviderOptions _fallback;
    private readonly ILogger<ChatClientFactory> _logger;

    public ChatClientFactory(IProviderRepository providers, IOptions<ProviderOptions> fallback, ILogger<ChatClientFactory> logger)
    {
        _providers = providers;
        _fallback = fallback.Value;
        _logger = logger;
    }

    public async Task<ChatEndpoint?> ResolveAsync(string? modelOverride, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["Operation"] = "ResolveEndpoint" });
        var sw = Stopwatch.StartNew();

        var active = await _providers.GetActiveProviderAsync();
        if (active is not null && !string.IsNullOrWhiteSpace(active.BaseUrl))
        {
            var model = modelOverride;
            if (string.IsNullOrWhiteSpace(model))
                model = (await _providers.GetDefaultModelAsync(active.Id))?.Model;
            if (string.IsNullOrWhiteSpace(model))
                model = (await _providers.ListModelsAsync(active.Id)).FirstOrDefault()?.Model;
            if (string.IsNullOrWhiteSpace(model))
                model = _fallback.Model;

            var endpoint = new ChatEndpoint(active.Id, active.BaseUrl, active.ApiKey, model!);
            _logger.LogInformation("Resolved endpoint from active provider {ProviderId} model {Model} in {ElapsedMs}ms", active.Id, model, sw.ElapsedMilliseconds);
            return endpoint;
        }

        if (_fallback.IsConfigured)
        {
            var endpoint = new ChatEndpoint(null, _fallback.BaseUrl, _fallback.ApiKey, modelOverride ?? _fallback.Model);
            _logger.LogInformation("Resolved endpoint from fallback config model {Model} in {ElapsedMs}ms", endpoint.Model, sw.ElapsedMilliseconds);
            return endpoint;
        }

        _logger.LogWarning("No active provider and fallback not configured");
        return null;
    }

    public IChatClient GetClient(ChatEndpoint endpoint)
    {
        var key = $"{endpoint.BaseUrl}|{endpoint.ApiKey}|{endpoint.Model}";
        return Clients.GetOrAdd(key, _ =>
        {
            _logger.LogInformation("Creating new IChatClient for {BaseUrl} model {Model}", endpoint.BaseUrl, endpoint.Model);
            var credential = new ApiKeyCredential(
                string.IsNullOrWhiteSpace(endpoint.ApiKey) ? "not-required" : endpoint.ApiKey);
            var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(endpoint.BaseUrl) };
            var openAi = new OpenAIClient(credential, clientOptions);
            var chatClient = openAi.GetChatClient(endpoint.Model).AsIChatClient();
            return new DiagnosticsChatClient(chatClient, _logger);
        });
    }
}
