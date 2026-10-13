using System.Text.Json;
using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.Context;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Memory;
using AgentWorkbench.ModelRuntime;
using AgentWorkbench.Observability;
using AgentWorkbench.Providers;
using AgentWorkbench.Safety;
using AgentWorkbench.ToolExecution;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentWorkbench.EvaluationTests.Fixtures;

/// <summary>
/// 内存轨迹存储：不落盘，按保存顺序编号（runId 从 1 起）。
/// <see cref="FailSaves"/> 置真时模拟"账本写不进去"——用于验证「落库失败不得掩盖原始异常」（不变量 3.10 / 6.6）。
/// </summary>
internal sealed class RecordingTraceStore : IExecutionTraceStore
{
    public List<ExecutionTrace> Saved { get; } = [];

    /// <summary>置真后 <see cref="SaveAsync"/> 一律抛（模拟仓储故障）。</summary>
    public bool FailSaves { get; set; }

    public Task<long> SaveAsync(ExecutionTrace trace, CancellationToken cancellationToken = default)
    {
        if (FailSaves)
            throw new InvalidOperationException("故障注入：账本写入失败");

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

/// <summary>一次「以抛出收场」的 run：异常 + 计数器（失败 / 取消 / 超时三条路径没有 HarnessRunResult）。</summary>
internal sealed record HarnessFailure(string Query, Exception Exception, int ToolCalls, int ModelCalls);

/// <summary>
/// 一次 run 在账本里留下的事实（成功与失败路径都填，未落库时 <see cref="Persisted"/> 为假）。
/// 评测的判分对象是「账本说了什么」而不是「结果对象说了什么」——后者在抛异常的路径上根本不存在。
/// </summary>
internal sealed record LedgerFact(
    bool Persisted, string? Status, string? ErrorCode, int Iterations, IReadOnlyList<ExecutionStep> Steps)
{
    /// <summary>未落库（仓储故障被吞掉）时的占位事实。</summary>
    public static LedgerFact NotPersisted { get; } = new(false, null, null, 0, []);

    public static LedgerFact From(ExecutionTrace trace)
        => new(true, trace.Status, trace.ErrorCode, trace.Iterations, trace.Steps);
}

/// <summary>一条待判分的评测样本：输入 + 可回放的对话 + 账本事实（三者按 query 对齐）。</summary>
internal sealed record EvalCase(string Query, AgentResponse Response, LedgerFact Ledger);

/// <summary>
/// 会话存储探针：记录每次写回，重点是写回令牌是否「不可取消」——
/// 调用方已取消时仍必须把会话写回去（不变量 3.7），实现手段就是传 <c>CancellationToken.None</c>。
/// </summary>
internal sealed class RecordingSessionStore : ISessionStore
{
    private readonly SessionStore _inner = new(Options.Create(new MemoryOptions()), TimeProvider.System);

    /// <summary>写回记录：(sessionId, 令牌是否可取消, run 的终态未知时也可断言调用次数)。</summary>
    public List<(string SessionId, bool TokenCanBeCanceled)> Saves { get; } = [];

    public Task<AgentSession> GetOrCreateAsync(
        string sessionId, ISessionFactory factory, CancellationToken cancellationToken = default)
        => _inner.GetOrCreateAsync(sessionId, factory, cancellationToken);

    public Task SaveAsync(
        string sessionId, ISessionFactory factory, AgentSession session, CancellationToken cancellationToken = default)
    {
        Saves.Add((sessionId, cancellationToken.CanBeCanceled));
        return _inner.SaveAsync(sessionId, factory, session, cancellationToken);
    }
}

/// <summary>
/// 被测 Harness 装配：与生产 <c>AgentHarnessService</c> 同一构造路径，
/// 但模型是脚本化的、目录是固定的、轨迹是内存的——零文件、零网络、零外部 provider（《标准》§5 隔离）。
/// 同时把「跑一批 → 造 AgentResponse → MAF 批量判分 → 断言容器」接成一条流水线。
/// </summary>
internal sealed class HarnessUnderTest
{
    private readonly List<EvalCase> _cases = [];
    private readonly HarnessAgent _agent;

    private HarnessUnderTest(
        IAgentHarness harness, HarnessAgent agent,
        ScriptedChatClient client, FaultInjectorTool tool, RecordingTraceStore traces,
        RecordingSessionStore? sessions)
    {
        _agent = agent;
        Harness = harness;
        Client = client;
        Tool = tool;
        Traces = traces;
        Sessions = sessions;
    }

    /// <summary>被测 Harness 门面。</summary>
    public IAgentHarness Harness { get; }

    /// <summary>脚本化模型客户端（可观测模型请求次数）。</summary>
    public ScriptedChatClient Client { get; }

    /// <summary>故障注入工具（可观测工具调用次数 / 取消穿透 / 外部闸门）。</summary>
    public FaultInjectorTool Tool { get; }

    /// <summary>内存轨迹存储（可断言落账，也可注入写失败）。</summary>
    public RecordingTraceStore Traces { get; }

    /// <summary>会话存储探针；仅当装配时传入才非空（不变量 3.7 用）。</summary>
    public RecordingSessionStore? Sessions { get; }

    /// <summary>已跑出的样本（成功与失败路径都进），按跑的顺序排列。</summary>
    public IReadOnlyList<EvalCase> Cases => _cases;

    /// <summary>query → 账本事实。自建 <see cref="EvalCheck"/> 靠它把判分落回账本（EvalItem 只带 query）。</summary>
    public IReadOnlyDictionary<string, LedgerFact> Ledger => _cases.ToDictionary(c => c.Query, c => c.Ledger);

    /// <summary>
    /// 装配一台被测 Harness。
    /// </summary>
    /// <param name="script">按模型请求序号（从 1 起）返回预设响应（也可直接抛异常，模拟模型侧故障）。</param>
    /// <param name="loopOptions">执行控制旋钮（迭代预算 / run 级超时 / 重复检测 / token 预算）。</param>
    /// <param name="runtime">模型运行时替身；传 <see cref="MeteredModelRuntime"/> 才能走到 token 预算路径。
    /// 多台机器可挂同一个 <see cref="UsageMeter"/>，用于复现并发用量串扰。</param>
    /// <param name="sessions">会话存储探针；传入后可断言「取消时会话仍写回」（不变量 3.7）。</param>
    /// <param name="traceStoreShouldFail">置真后账本写入一律抛，模拟仓储故障（不变量 3.10 / 6.6）。</param>
    /// <param name="extraTools">追加到目录里的额外工具（如 <see cref="LongResultTool"/>）；默认只有故障注入工具。</param>
    /// <param name="approval">审批策略；默认全部 Auto（与生产 appsettings 默认一致），
    /// 传别的策略即可让某个工具落到 Ask / Deny 分支。</param>
    public static HarnessUnderTest Create(
        Func<int, ChatResponse> script,
        AgentLoopOptions loopOptions,
        IModelRuntime? runtime = null,
        RecordingSessionStore? sessions = null,
        bool traceStoreShouldFail = false,
        IReadOnlyList<AITool>? extraTools = null,
        IToolApprovalPolicy? approval = null)
    {
        var client = new ScriptedChatClient(script);
        var tool = new FaultInjectorTool();
        var traces = new RecordingTraceStore { FailSaves = traceStoreShouldFail };
        var tools = new List<AITool>(tool.GetTools());
        if (extraTools is not null)
            tools.AddRange(extraTools);
        var harness = Build(
            client, runtime ?? new PassthroughModelRuntime(), tools, traces, loopOptions, sessions,
            approval ?? AutoToolApprovalPolicy.Instance);
        var agent = new HarnessAgent(harness);
        return new HarnessUnderTest(harness, agent, client, tool, traces, sessions);
    }

    /// <summary>跑一次 run；取消 / 失败路径按原语义抛出（用 <see cref="RunExpectingFailureAsync"/> 捕获）。</summary>
    public async Task<HarnessOutcome> RunAsync(string query, CancellationToken cancellationToken = default)
    {
        var result = await Harness.RunAsync(new HarnessRunRequest(null, query), cancellationToken);
        var trace = Traces.Saved[^1];
        var outcome = new HarnessOutcome(query, result, trace, Tool.Calls, Client.Calls);
        _cases.Add(new EvalCase(query, HarnessConversation.ToAgentResponse(query, result), LedgerFact.From(trace)));
        return outcome;
    }

    /// <summary>
    /// 跑一次「必定以抛出收场」的 run 并捕获它：失败 / 取消 / 超时三条路径没有结果对象，
    /// 可观测量只有「抛了什么」与「账本留下了什么」。run 若正常返回，本方法自己抛——
    /// 否则「期望失败的路径悄悄成功了」会被当成通过（假绿）。
    /// </summary>
    public async Task<HarnessFailure> RunExpectingFailureAsync(string query, CancellationToken cancellationToken = default)
    {
        var before = Traces.Saved.Count;
        Exception? captured = null;
        try
        {
            await Harness.RunAsync(new HarnessRunRequest(null, query), cancellationToken);
        }
        catch (Exception exception)
        {
            captured = exception;
        }

        if (captured is null)
            throw new InvalidOperationException($"期望 run 以抛出收场，但它正常返回了：{query}");

        var trace = Traces.Saved.Count > before ? Traces.Saved[^1] : null;
        _cases.Add(new EvalCase(query, HarnessConversation.FromTrace(query, trace),
            trace is null ? LedgerFact.NotPersisted : LedgerFact.From(trace)));
        return new HarnessFailure(query, captured, Tool.Calls, Client.Calls);
    }

    /// <summary>
    /// 对本夹具已跑出的全部样本做 MAF 批量判分（回放轨：不重跑 agent）。
    /// 判据由调用方以 <see cref="EvalCheck"/> 数组给出——MAF 只提供容器与断言，判定逻辑全部自建。
    /// </summary>
    public async Task<AgentEvaluationResults> EvaluateAsync(
        string evalName, EvalCheck[] checks, CancellationToken cancellationToken = default)
    {
        var responses = _cases.Select(item => item.Response).ToList();
        var queries = _cases.Select(item => item.Query).ToList();
        return await _agent.EvaluateAsync(responses, queries, new LocalEvaluator(checks), evalName, null, null, cancellationToken);
    }

    private static IAgentHarness Build(
        IChatClient inner, IModelRuntime runtime, IReadOnlyList<AITool> tools,
        IExecutionTraceStore traces, AgentLoopOptions loopOptions, RecordingSessionStore? sessions,
        IToolApprovalPolicy approval)
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
                Options.Create(new ToolExecutionOptions()), approval, NullLoggerFactory.Instance, services),
            Options.Create(new ContextOptions { Enabled = false }),
            (ISessionStore?)sessions ?? new SessionStore(Options.Create(new MemoryOptions()), TimeProvider.System),
            Options.Create(new MemoryOptions()),
            new AsyncLocalMemoryScopeAccessor(),
            new AgentLoopService(Options.Create(loopOptions), NullLogger<AgentLoopService>.Instance),
            traces,
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
        var result = await harness.RunAsync(new HarnessRunRequest(null, query), cancellationToken);
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

    /// <summary>
    /// 账本轨迹 → 对话（失败 / 取消 / 超时路径专用）：这些路径没有 <see cref="HarnessRunResult"/>，
    /// 唯一的可观测量就是账本里留下的步骤。重建后仍可被 <see cref="EvalChecks"/> 判分——
    /// 「异常路径也进同一个评测容器」是批量驱动的关键，否则失败路径只能靠散落的断言。
    /// </summary>
    public static AgentResponse FromTrace(string query, ExecutionTrace? trace)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        if (trace is null)
            return new AgentResponse(messages);

        var index = 0;
        foreach (var step in trace.Steps)
        {
            if (!string.Equals(step.Kind, "tool", StringComparison.Ordinal) || step.Tool is null)
                continue;

            var callId = $"step-{index++}";
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent(callId, step.Tool, ParseArguments(step.Arguments))]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(callId, step.Error ?? step.Result)]));
        }

        var answer = trace.Steps.LastOrDefault(step => string.Equals(step.Kind, "assistant", StringComparison.Ordinal))?.Result;
        if (!string.IsNullOrEmpty(answer))
            messages.Add(new ChatMessage(ChatRole.Assistant, answer));

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
