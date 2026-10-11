using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.AgentHarness;

/// <summary>Agent 运行时门面：一次 run 的编排入口（loop / context / memory / tools / trace 各由独立模块提供）。</summary>
public interface IAgentHarness
{
    Task<HarnessCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken cancellationToken = default);

    Task<HarnessRunResult?> GetRunAsync(long runId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AgentHarnessService : IAgentHarness
{
    private const int MaxAssistantChars = 400;
    private const float Temperature = 0.7f;

    private readonly IChatClientFactory _factory;
    private readonly IModelRuntime _runtime;
    private readonly IToolCatalog _tools;
    private readonly IToolExecutionPipeline _pipeline;
    private readonly IContextBuilder _context;
    private readonly IOptions<ContextOptions> _contextOptions;
    private readonly ISessionStore _sessions;
    private readonly IOptions<MemoryOptions> _memoryOptions;
    private readonly IAgentLoop _loop;
    private readonly IExecutionTraceStore _traces;
    private readonly IKnowledgeEngine _knowledge;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;
    private readonly ILogger<AgentHarnessService> _logger;

    public AgentHarnessService(
        IChatClientFactory factory,
        IModelRuntime runtime,
        IToolCatalog tools,
        IToolExecutionPipeline pipeline,
        IContextBuilder context,
        IOptions<ContextOptions> contextOptions,
        ISessionStore sessions,
        IOptions<MemoryOptions> memoryOptions,
        IAgentLoop loop,
        IExecutionTraceStore traces,
        IKnowledgeEngine knowledge,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        ILogger<AgentHarnessService> logger)
    {
        _factory = factory;
        _runtime = runtime;
        _tools = tools;
        _pipeline = pipeline;
        _context = context;
        _contextOptions = contextOptions;
        _sessions = sessions;
        _memoryOptions = memoryOptions;
        _loop = loop;
        _traces = traces;
        _knowledge = knowledge;
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
            Context: _contextOptions.Value.Enabled,
            Memory: _memoryOptions.Value.SessionEnabled,
            Tools: _tools.GetToolNames());
    }

    public async Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken cancellationToken = default)
    {
        var endpoint = await _factory.ResolveAsync(null, cancellationToken)
            ?? throw new HarnessModelNotConfiguredException();

        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString("N")
            : request.SessionId!;
        var sw = Stopwatch.StartNew();

        // 模型请求统一走 ModelRuntime（超时 / 重试 / 用量），工具环预算来自 AgentLoop，工具授权已由 ToolCatalog 按白名单裁剪
        var chatClient = _runtime.Wrap(_factory.GetClient(endpoint));
        var execution = _pipeline.Create(chatClient, new ToolExecutionLimits(_loop.MaxIterations));
        var agent = BuildAgent(execution.Client);

        var userText = request.Input;
        if (request.UseKnowledge && _contextOptions.Value.Enabled)
        {
            var hits = await _knowledge.RetrieveAsync(request.Input, _contextOptions.Value.RagTopK, cancellationToken);
            userText = _context.Compose(request.Input, hits);
        }

        var messages = new List<ChatMessage> { new(ChatRole.User, userText) };

        var session = _memoryOptions.Value.SessionEnabled
            ? await _sessions.GetOrCreateAsync(sessionId, token => agent.CreateSessionAsync(token).AsTask(), cancellationToken)
            : await agent.CreateSessionAsync(cancellationToken);

        try
        {
            // 执行控制：run 级超时由 AgentLoop 施加（超时抛 AgentRunTimeoutException）
            var response = await _loop.ExecuteAsync(
                async token => await agent.RunAsync(
                    messages, session,
                    new ChatClientAgentRunOptions(new ChatOptions { Temperature = Temperature }),
                    token),
                cancellationToken);

            var text = response.Text ?? string.Empty;
            var steps = BuildSteps(execution.Traces, text);
            var iterations = steps.Max(s => s.Iteration) + 1;
            sw.Stop();

            var trace = ExecutionTrace.Success(
                sessionId, iterations, endpoint.Model, sw.ElapsedMilliseconds, ToTraceSteps(steps));
            var runId = await _traces.SaveAsync(trace, cancellationToken);

            _logger.LogInformation(
                "Harness run {RunId} session={SessionId} iterations={Iterations} tools={ToolSteps} failedToolSteps={FailedToolSteps} in {ElapsedMs}ms",
                runId, sessionId, iterations,
                steps.Count(s => s.Kind == "tool"), steps.Count(s => s.Error is not null),
                sw.ElapsedMilliseconds);

            return new HarnessRunResult(runId, sessionId, text, iterations, endpoint.Model, sw.ElapsedMilliseconds, steps);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            await PersistFailureAsync(sessionId, endpoint.Model, execution.Traces, sw.ElapsedMilliseconds, "运行超时（timeout）");
            throw new AgentRunTimeoutException(_loop.TimeoutSeconds);
        }
        catch (TimeoutException ex)
        {
            sw.Stop();
            await PersistFailureAsync(sessionId, endpoint.Model, execution.Traces, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await PersistFailureAsync(sessionId, endpoint.Model, execution.Traces, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }

    public async Task<HarnessRunResult?> GetRunAsync(long runId, CancellationToken cancellationToken = default)
    {
        var trace = await _traces.GetAsync(runId, cancellationToken);
        if (trace is null)
            return null;
        return new HarnessRunResult(
            runId, trace.SessionId,
            trace.Steps.LastOrDefault(s => s.Kind == "assistant")?.Result ?? string.Empty,
            trace.Iterations, trace.Model, trace.LatencyMs,
            trace.Steps.Select(s => new HarnessStep(s.Iteration, s.Kind, s.Tool, s.Arguments, s.Result, s.Error)).ToList());
    }

    private ChatClientAgent BuildAgent(IChatClient client)
    {
        var options = new ChatClientAgentOptions
        {
            Name = "AgentHarness",
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions()),
            ChatOptions = new ChatOptions { Tools = [.. _tools.GetTools()], Temperature = Temperature }
        };
        return new ChatClientAgent(client, options, _loggerFactory, _services);
    }

    private async Task PersistFailureAsync(
        string sessionId, string model, IReadOnlyList<ToolCallTrace> traces, long elapsedMs, string error)
    {
        try
        {
            var steps = BuildSteps(traces, assistantText: null);
            var iterations = steps.Count > 0 ? steps.Max(s => s.Iteration) : 0;
            await _traces.SaveAsync(
                ExecutionTrace.Failure(sessionId, iterations, model, elapsedMs, error, ToTraceSteps(steps)),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记录失败 run 轨迹时出错 session={SessionId}", sessionId);
        }
    }

    private static IReadOnlyList<HarnessStep> BuildSteps(IReadOnlyList<ToolCallTrace> traces, string? assistantText)
    {
        var steps = traces
            .Select(t => new HarnessStep(t.Iteration, "tool", t.Name, t.Arguments, t.Result, t.Error))
            .ToList();
        if (assistantText is null)
            return steps;

        var finalIteration = steps.Select(s => s.Iteration).DefaultIfEmpty(-1).Max() + 1;
        steps.Add(new HarnessStep(finalIteration, "assistant", null, null, Truncate(assistantText)));
        return steps;
    }

    private static IReadOnlyList<ExecutionStep> ToTraceSteps(IEnumerable<HarnessStep> steps)
        => steps.Select(s => new ExecutionStep(s.Iteration, s.Kind, s.Tool, s.Arguments, s.Result, s.Error)).ToList();

    private static string? Truncate(string? value)
        => value is null ? null : value.Length <= MaxAssistantChars ? value : value[..MaxAssistantChars];
}
