using System.Text.Json;
using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.Context;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Memory;
using AgentWorkbench.ModelRuntime;
using AgentWorkbench.Observability;
using AgentWorkbench.Providers;
using AgentWorkbench.ToolExecution;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentWorkbench.EvaluationTests.Fixtures;

/// <summary>内存轨迹存储：不落盘，按保存顺序编号（runId 从 1 起）。</summary>
internal sealed class RecordingTraceStore : IExecutionTraceStore
{
    public List<ExecutionTrace> Saved { get; } = [];

    public Task<long> SaveAsync(ExecutionTrace trace, CancellationToken cancellationToken = default)
    {
        Saved.Add(trace);
        return Task.FromResult((long)Saved.Count);
    }

    public Task<ExecutionTrace?> GetAsync(long runId, CancellationToken cancellationToken = default)
        => Task.FromResult(runId >= 1 && runId <= Saved.Count ? Saved[(int)runId - 1] : null);
}

/// <summary>一次评测 run 的全部可观测量：结果 + 轨迹 + 计数器。</summary>
/// <param name="Query">本次 run 的输入（HarnessRunResult 不带输入，评测集需要它对齐 query/response）。</param>
internal sealed record HarnessOutcome(
    string Query, HarnessRunResult Result, ExecutionTrace Trace, int ToolCalls, int ModelCalls);

/// <summary>
/// 被测 Harness 装配：与生产 <c>AgentHarnessService</c> 同一构造路径，
/// 但模型是脚本化的、目录是固定的、轨迹是内存的——零文件、零网络、零外部 provider（《标准》§5 隔离）。
/// 同时把「跑一批 → 造 AgentResponse → MAF 批量判分 → 断言容器」接成一条流水线。
/// </summary>
internal sealed class HarnessUnderTest
{
    private readonly List<HarnessOutcome> _outcomes = [];
    private readonly HarnessAgent _agent;

    private HarnessUnderTest(
        IAgentHarness harness, HarnessAgent agent,
        ScriptedChatClient client, FaultInjectorTool tool, RecordingTraceStore traces)
    {
        _agent = agent;
        Harness = harness;
        Client = client;
        Tool = tool;
        Traces = traces;
    }

    /// <summary>被测 Harness 门面。</summary>
    public IAgentHarness Harness { get; }

    /// <summary>脚本化模型客户端（可观测模型请求次数）。</summary>
    public ScriptedChatClient Client { get; }

    /// <summary>故障注入工具（可观测工具调用次数 / 取消穿透 / 外部闸门）。</summary>
    public FaultInjectorTool Tool { get; }

    /// <summary>内存轨迹存储（可断言落账）。</summary>
    public RecordingTraceStore Traces { get; }

    /// <summary>
    /// 装配一台被测 Harness。
    /// </summary>
    /// <param name="script">按模型请求序号（从 1 起）返回预设响应。</param>
    /// <param name="loopOptions">执行控制旋钮（迭代预算 / run 级超时 / 重复检测 / token 预算）。</param>
    /// <param name="runtime">模型运行时替身；传 <see cref="MeteredModelRuntime"/> 才能走到 token 预算路径。
    /// 多台机器可挂同一个 <see cref="UsageMeter"/>，用于复现并发用量串扰。</param>
    public static HarnessUnderTest Create(
        Func<int, ChatResponse> script,
        AgentLoopOptions loopOptions,
        IModelRuntime? runtime = null)
    {
        var client = new ScriptedChatClient(script);
        var tool = new FaultInjectorTool();
        var traces = new RecordingTraceStore();
        var harness = Build(client, runtime ?? new PassthroughModelRuntime(), [.. tool.GetTools()], traces, loopOptions);
        var agent = new HarnessAgent(harness);
        return new HarnessUnderTest(harness, agent, client, tool, traces);
    }

    /// <summary>跑一次 run；取消 / 失败路径按原语义抛出（调用方断言异常与落账）。</summary>
    public async Task<HarnessOutcome> RunAsync(string query, CancellationToken cancellationToken = default)
    {
        var result = await Harness.RunAsync(new HarnessRunRequest(null, query, UseKnowledge: false), cancellationToken);
        var outcome = new HarnessOutcome(query, result, Traces.Saved[^1], Tool.Calls, Client.Calls);
        _outcomes.Add(outcome);
        return outcome;
    }

    /// <summary>
    /// 对本夹具已跑出的全部 outcome 做 MAF 批量判分（回放轨：不重跑 agent）。
    /// 判据由调用方以 <see cref="EvalCheck"/> 数组给出——MAF 只提供容器与断言，判定逻辑全部自建。
    /// </summary>
    public async Task<AgentEvaluationResults> EvaluateAsync(
        string evalName, EvalCheck[] checks, CancellationToken cancellationToken = default)
    {
        var responses = _outcomes.Select(outcome => HarnessConversation.ToAgentResponse(outcome.Query, outcome.Result)).ToList();
        var queries = _outcomes.Select(outcome => outcome.Query).ToList();
        return await _agent.EvaluateAsync(responses, queries, new LocalEvaluator(checks), evalName, null, null, cancellationToken);
    }

    private static IAgentHarness Build(
        IChatClient inner, IModelRuntime runtime, IReadOnlyList<AITool> tools,
        IExecutionTraceStore traces, AgentLoopOptions loopOptions)
    {
        var factory = new Mock<IChatClientFactory>(MockBehavior.Strict);
        var endpoint = new ChatEndpoint(1, "http://test", "key", "model");
        factory.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(endpoint);
        factory.Setup(f => f.GetClient(It.IsAny<ChatEndpoint>())).Returns(inner);

        var services = new ServiceCollection().BuildServiceProvider();

        return new AgentHarnessService(
            factory.Object,
            runtime,
            new FakeToolCatalog(tools),
            new ToolExecutionPipeline(
                Options.Create(new ToolExecutionOptions()), NullLoggerFactory.Instance, services),
            new ContextBuilder(),
            Options.Create(new ContextOptions { Enabled = false }),
            new SessionStore(Options.Create(new MemoryOptions()), TimeProvider.System),
            Options.Create(new MemoryOptions()),
            new AsyncLocalMemoryScopeAccessor(),
            new AgentLoopService(Options.Create(loopOptions), NullLogger<AgentLoopService>.Instance),
            traces,
            Mock.Of<IKnowledgeEngine>(),
            NullLoggerFactory.Instance,
            services,
            NullLogger<AgentHarnessService>.Instance);
    }
}

/// <summary>
/// 把 Harness 的一次 run 包成 <see cref="AIAgent"/>：身份即被测系统本身。
/// 抽象面按 Microsoft.Agents.AI 1.23.0 实测（反射探针核对）：五个 Core 钩子。
/// 回放轨（EvaluateAsync 预录 responses）不会调用它；真实驱动轨留给后续接入。
/// </summary>
internal sealed class HarnessAgent(IAgentHarness harness) : AIAgent
{
    public override string Name => "agentworkbench-harness";

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
        => RunHarnessAsync(messages, cancellationToken);

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
        => throw new NotSupportedException("harness 评测只走非流式路径");

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("harness 评测（回放轨）不创建会话");

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
        => throw new NotSupportedException("harness 评测（回放轨）不序列化会话");

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken)
        => throw new NotSupportedException("harness 评测（回放轨）不反序列化会话");

    private async Task<AgentResponse> RunHarnessAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var query = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        var result = await harness.RunAsync(new HarnessRunRequest(null, query, UseKnowledge: false), cancellationToken);
        return HarnessConversation.ToAgentResponse(query, result);
    }
}

/// <summary>
/// 轨迹 → 对话：把 <see cref="HarnessRunResult"/> 重建成 MAF 评测看得懂的消息序列
/// （user → 每步 assistant(FunctionCall) + tool(FunctionResult) → 最终 assistant 文本）。
/// 工具调用能不能被 <see cref="EvalChecks"/> 看见，全靠这一步保真。
/// </summary>
internal static class HarnessConversation
{
    public static AgentResponse ToAgentResponse(string query, HarnessRunResult result)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        var index = 0;

        foreach (var step in result.Steps)
        {
            if (!string.Equals(step.Kind, "tool", StringComparison.Ordinal) || step.Tool is null)
                continue;

            var callId = $"step-{index++}";
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(callId, step.Tool, ParseArguments(step.Arguments))]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(callId, step.Error ?? step.Result)]));
        }

        if (!string.IsNullOrEmpty(result.Text))
            messages.Add(new ChatMessage(ChatRole.Assistant, result.Text));

        return new AgentResponse(messages);
    }

    private static IDictionary<string, object?>? ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        using var document = JsonDocument.Parse(json);
        var arguments = new Dictionary<string, object?>();
        foreach (var property in document.RootElement.EnumerateObject())
            arguments[property.Name] = Convert(property.Value);
        return arguments;
    }

    private static object? Convert(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
