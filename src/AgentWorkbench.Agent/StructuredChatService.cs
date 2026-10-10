using System.Diagnostics;
using AgentWorkbench.Providers;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Agent;

/// <inheritdoc />
public sealed class StructuredChatService : IStructuredChatService
{
    private readonly IChatClientFactory _factory;
    private readonly ProviderOptions _options;
    private readonly ILogger<StructuredChatService> _logger;

    public StructuredChatService(
        IChatClientFactory factory,
        IOptions<ProviderOptions> options,
        ILogger<StructuredChatService> logger)
    {
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        => (await _factory.ResolveAsync(null, cancellationToken)) is not null;

    /// <inheritdoc />
    public async Task<T?> GetJsonAsync<T>(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var endpoint = await _factory.ResolveAsync(null, cancellationToken);
        if (endpoint is null)
        {
            _logger.LogWarning("GetJsonAsync: 模型未配置");
            return default;
        }

        var client = _factory.GetClient(endpoint);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt + "\n\n【输出格式】只输出一个合法 JSON 对象，不要包含解释、前言或 Markdown 代码块。"),
            new(ChatRole.User, userPrompt)
        };

        try
        {
            var response = await client.GetResponseAsync<T>(
                messages, new ChatOptions { Temperature = _options.Temperature }, cancellationToken: cancellationToken);
            _logger.LogInformation("GetJsonAsync ok: Model={Model} Type={Type} ElapsedMs={ElapsedMs}", endpoint.Model, typeof(T).Name, sw.ElapsedMilliseconds);
            return response.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "结构化输出解析失败，追加修复提示后重试一次");
            messages.Add(new ChatMessage(ChatRole.User, "上一次输出不是合法 JSON。请严格只输出符合要求的 JSON 对象。"));
            var retry = await client.GetResponseAsync<T>(
                messages, new ChatOptions { Temperature = 0 }, cancellationToken: cancellationToken);
            _logger.LogInformation("GetJsonAsync retry ok: Model={Model} Type={Type} ElapsedMs={ElapsedMs}", endpoint.Model, typeof(T).Name, sw.ElapsedMilliseconds);
            return retry.Result;
        }
    }
}
