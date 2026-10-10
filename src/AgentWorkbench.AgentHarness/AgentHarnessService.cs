using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Providers;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.AgentHarness;

/// <summary>Agent Harness（运行时横切轴）：loop + context + memory + tools + trace，基于 MAF。</summary>
public interface IAgentHarness
{
    Task<HarnessCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken cancellationToken = default);

    Task<HarnessRunResult?> GetRunAsync(long runId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AgentHarnessService : IAgentHarness
{
    private const int MaxResultChars = 400;

    private readonly IChatClientFactory _factory;
    private readonly HarnessOptions _options;
    private readonly IReadOnlyList<IAgentToolProvider> _toolProviders;
    private readonly IKnowledgeEngine _knowledge;
    private readonly IAgentRunRepository _repository;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<AgentHarnessService> _logger;

    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);

    public AgentHarnessService(
        IChatClientFactory factory,
        IOptions<HarnessOptions> options,
        IEnumerable<IAgentToolProvider> toolProviders,
        IKnowledgeEngine knowledge,
        IAgentRunRepository repository,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        ILogger<AgentHarnessService> logger)
    {
        _factory = factory;
        _options = options.Value;
        _toolProviders = toolProviders.ToList();
        _knowledge = knowledge;
        _repository = repository;
        _loggerFactory = loggerFactory;
        _services = services;
        _logger = logger;
    }

    public async Task<HarnessCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var llm = await _factory.ResolveAsync(null, cancellationToken) is not null;
        return new HarnessCapabilities(
            Loop: true,
            Llm: llm,
            Context: _options.Context.Enabled,
            Memory: _options.Memory.SessionEnabled,
            Tools: AllowedToolNames());
    }

    public async Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken cancellationToken = default)
    {
        var endpoint = await _factory.ResolveAsync(null, cancellationToken)
            ?? throw new HarnessModelNotConfiguredException();

        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString("N")
            : request.SessionId!;
        var steps = new List<HarnessStep>();
        var sw = Stopwatch.StartNew();

        var tools = _toolProviders
            .SelectMany(p => p.GetTools())
            .Where(t => _options.Tools.Whitelist.Contains(t.Name, StringComparer.Ordinal))
            .ToList();

        var agent = BuildAgent(endpoint, tools, steps);

        var userText = request.Input;
        if (request.UseKnowledge && _options.Context.Enabled)
        {
            var hits = await _knowledge.RetrieveAsync(request.Input, _options.Context.RagTopK, cancellationToken);
            var context = BuildContext(hits);
            if (context.Length > 0)
                userText = context + "\n\n" + request.Input;
        }

        var messages = new List<ChatMessage> { new(ChatRole.User, userText) };

        var session = _options.Memory.SessionEnabled
            ? await GetOrCreateSessionAsync(sessionId, agent, cancellationToken)
            : await agent.CreateSessionAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.Loop.TimeoutSeconds));

        try
        {
            var response = await agent.RunAsync(
                messages, session,
                new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.7f }),
                timeout.Token);

            var text = response.Text ?? string.Empty;
            var lastToolIteration = steps.Where(s => s.Kind == "tool").Select(s => s.Iteration).DefaultIfEmpty(-1).Max();
            var finalIteration = lastToolIteration + 1;
            steps.Add(new HarnessStep(finalIteration, "assistant", null, null, Truncate(text)));
            var iterations = steps.Max(s => s.Iteration) + 1;
            sw.Stop();

            var run = new AgentRunRecord
            {
                SessionId = sessionId,
                Status = "succeeded",
                Iterations = iterations,
                Model = endpoint.Model,
                LatencyMs = sw.ElapsedMilliseconds,
                CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
                FinishedAt = DateTimeOffset.UtcNow.ToString("o")
            };
            var runId = await _repository.InsertRunAsync(run);
            await _repository.InsertStepsAsync(runId, ToStepRecords(steps));

            _logger.LogInformation("Harness run {RunId} session={SessionId} iterations={Iterations} tools={ToolSteps} in {ElapsedMs}ms",
                runId, sessionId, iterations, steps.Count(s => s.Kind == "tool"), sw.ElapsedMilliseconds);

            return new HarnessRunResult(runId, sessionId, text, iterations, endpoint.Model, sw.ElapsedMilliseconds, steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            await PersistFailureAsync(sessionId, endpoint.Model, steps, sw.ElapsedMilliseconds, "运行超时（timeout）");
            throw new TimeoutException($"Harness 运行超时（{_options.Loop.TimeoutSeconds}s）");
        }
        catch (Exception ex)
        {
            sw.Stop();
            await PersistFailureAsync(sessionId, endpoint.Model, steps, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }

    public async Task<HarnessRunResult?> GetRunAsync(long runId, CancellationToken cancellationToken = default)
    {
        var run = await _repository.GetRunAsync(runId);
        if (run is null)
            return null;
        var steps = await _repository.GetStepsAsync(runId);
        return new HarnessRunResult(
            run.Id, run.SessionId,
            steps.LastOrDefault(s => s.Kind == "assistant")?.Result ?? string.Empty,
            run.Iterations, run.Model, run.LatencyMs ?? 0,
            steps.Select(s => new HarnessStep(s.Iteration, s.Kind, s.Tool, s.Arguments, s.Result)).ToList());
    }

    private ChatClientAgent BuildAgent(ChatEndpoint endpoint, List<AITool> tools, List<HarnessStep> steps)
    {
        var baseClient = _factory.GetClient(endpoint);
        var invoking = new FunctionInvokingChatClient(baseClient, _loggerFactory, _services)
        {
            MaximumIterationsPerRequest = _options.Loop.MaxIterations
        };
        invoking.FunctionInvoker = async (context, token) =>
        {
            steps.Add(new HarnessStep(context.Iteration, "tool", context.Function.Name, Serialize(context.Arguments), null));
            var result = await context.Function.InvokeAsync(context.Arguments, token);
            steps[^1] = steps[^1] with { Result = Truncate(result?.ToString()) };
            return result;
        };

        var options = new ChatClientAgentOptions
        {
            Name = "AgentHarness",
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions()),
            ChatOptions = new ChatOptions { Tools = tools, Temperature = 0.7f }
        };
        return new ChatClientAgent(invoking, options, _loggerFactory, _services);
    }

    private async Task<AgentSession> GetOrCreateSessionAsync(string sessionId, ChatClientAgent agent, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
            return existing;
        var session = await agent.CreateSessionAsync(cancellationToken);
        return _sessions.GetOrAdd(sessionId, session);
    }

    private IReadOnlyList<string> AllowedToolNames()
    {
        var available = _toolProviders.SelectMany(p => p.GetTools()).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        return _options.Tools.Whitelist.Where(available.Contains).ToList();
    }

    private async Task PersistFailureAsync(string sessionId, string model, List<HarnessStep> steps, long elapsedMs, string error)
    {
        try
        {
            var run = new AgentRunRecord
            {
                SessionId = sessionId,
                Status = "failed",
                Iterations = steps.Count > 0 ? steps.Max(s => s.Iteration) : 0,
                Model = model,
                LatencyMs = elapsedMs,
                Error = error,
                CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
                FinishedAt = DateTimeOffset.UtcNow.ToString("o")
            };
            var runId = await _repository.InsertRunAsync(run);
            await _repository.InsertStepsAsync(runId, ToStepRecords(steps));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记录失败 run 轨迹时出错 session={SessionId}", sessionId);
        }
    }

    private static IReadOnlyList<AgentStepRecord> ToStepRecords(IEnumerable<HarnessStep> steps)
        => steps.Select(s => new AgentStepRecord
        {
            Iteration = s.Iteration,
            Kind = s.Kind,
            Tool = s.Tool,
            Arguments = s.Arguments,
            Result = s.Result,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        }).ToList();

    private static string BuildContext(IReadOnlyList<RetrievalHit> hits)
    {
        if (hits.Count == 0)
            return string.Empty;
        var lines = hits.Select(h => $"《{h.FileName}》片段#{h.Seq} (p{h.PageFrom}-{h.PageTo}) :: {Snippet(h.Content)}");
        return "【知识库检索片段】仅供回答参考，需在结论中标注来源：\n" + string.Join("\n", lines);
    }

    private static string Serialize(AIFunctionArguments arguments)
        => JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()));

    private static string? Truncate(string? value)
        => value is null ? null : value.Length <= MaxResultChars ? value : value[..MaxResultChars];

    private static string Snippet(string content)
        => content.Length <= 120 ? content : content[..120];
}
