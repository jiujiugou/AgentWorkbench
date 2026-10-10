using System.ClientModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentWorkbench.Providers;
using AgentWorkbench.Shared.Options;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Agent;

/// <inheritdoc />
public sealed class ChatService : IChatService
{
    private readonly IChatClientFactory _factory;
    private readonly ProviderOptions _options;
    private readonly IReadOnlyList<AITool> _tools;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        IChatClientFactory factory,
        IOptions<ProviderOptions> options,
        IEnumerable<IAgentToolProvider> tools,
        ILogger<ChatService> logger)
    {
        _factory = factory;
        _options = options.Value;
        _tools = tools.SelectMany(t => t.GetTools()).ToArray();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var configured = (await _factory.ResolveAsync(null, cancellationToken)) is not null;
        _logger.LogDebug("IsConfiguredAsync: {Configured}", configured);
        return configured;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> history,
        string userMessage,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string? systemPrompt = null)
    {
        var sw = Stopwatch.StartNew();
        var endpoint = await _factory.ResolveAsync(null, cancellationToken)
            ?? throw new InvalidOperationException("模型未配置：请在「设置」页添加供应商与模型。");

        var chatClient = _factory.GetClient(endpoint);
        var effectiveSystemPrompt = systemPrompt ?? _options.SystemPrompt;
        var enabledTools = _options.SupportsTools ? _tools : Array.Empty<AITool>();
        var messages = BuildMessages(history, userMessage);

        _logger.LogInformation("StreamAsync started: ProviderId={ProviderId} Model={Model} HistoryTurns={Turns} Tools={Tools} SupportsTools={SupportsTools} HasSystemPrompt={HasPrompt}",
            endpoint.ProviderId, endpoint.Model, history.Count, enabledTools.Count, _options.SupportsTools, !string.IsNullOrWhiteSpace(effectiveSystemPrompt));

        var tokenCount = 0;
        var degraded = false;

        // 最多两次：第一次带工具；若模型/端点不支持 tools 返回 4xx，降级为无工具重试一次。
        while (true)
        {
            var tools = degraded ? Array.Empty<AITool>() : enabledTools;
            var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Temperature = _options.Temperature });
            var yielded = false;
            var retry = false;

            await using var enumerator = RunStreamAsync(chatClient, effectiveSystemPrompt, tools, messages, runOptions, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                AgentResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;
                    update = enumerator.Current;
                }
                catch (ClientResultException ex) when (!degraded && tools.Count > 0 && !yielded)
                {
                    _logger.LogWarning(ex, "模型返回客户端错误（Status={Status}，请求含 {ToolCount} 个工具），降级为无工具重试一次。",
                        ex.Status, tools.Count);
                    retry = true;
                    break;
                }

                if (!string.IsNullOrEmpty(update.Text))
                {
                    yielded = true;
                    tokenCount++;
                    yield return update.Text;
                }
            }

            if (!retry)
                break;
            degraded = true;
        }

        _logger.LogInformation("StreamAsync completed: Model={Model} Chunks={Chunks} Degraded={Degraded} ElapsedMs={ElapsedMs}",
            endpoint.Model, tokenCount, degraded, sw.ElapsedMilliseconds);
    }

    private static async IAsyncEnumerable<AgentResponseUpdate> RunStreamAsync(
        IChatClient chatClient,
        string? systemPrompt,
        IReadOnlyList<AITool> tools,
        IReadOnlyList<ChatMessage> messages,
        ChatClientAgentRunOptions runOptions,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var agent = new ChatClientAgent(
            chatClient,
            instructions: string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
            name: "AgentWorkbench",
            tools: tools.Count == 0 ? null : tools.ToList());

        var input = messages as List<ChatMessage> ?? messages.ToList();
        await foreach (var update in agent.RunStreamingAsync(input, options: runOptions, cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    private static List<ChatMessage> BuildMessages(IReadOnlyList<ChatTurn> history, string userMessage)
    {
        var messages = new List<ChatMessage>(history.Count + 1);
        foreach (var turn in history)
        {
            var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            messages.Add(new ChatMessage(role, turn.Content));
        }

        messages.Add(new ChatMessage(ChatRole.User, userMessage));
        return messages;
    }
}
