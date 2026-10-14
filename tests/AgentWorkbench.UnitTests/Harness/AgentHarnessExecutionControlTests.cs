using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Memory;
using AgentWorkbench.ModelRuntime;
using AgentWorkbench.Observability;
using AgentWorkbench.Providers;
using AgentWorkbench.Shared;
using AgentWorkbench.ToolExecution;
using AgentWorkbench.UnitTests.Conversation;
using AgentWorkbench.UnitTests.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.UnitTests.Harness;

/// <summary>
/// 判定型（端到端到落库那一层）：一次 run 的结局必须能分开 ——
/// 卡在同一个调用上 / token 超预算 / 跑满迭代 / 调用方取消 / 工具连续失败 / 墙钟超时。
/// 每个正例都配同形状的对照（只动旋钮），对照刻意落在「另一种结局」上，证明旋钮真的在起作用。
/// </summary>
public sealed class AgentHarnessExecutionControlTests
{
    [Fact]
    public async Task RepeatedIdenticalToolCalls_StopAsLoopDetected()
    {
        var tool = new FaultInjectorTool();
        var traces = new RecordingTraceStore();
        var harness = Build(
            new ScriptedChatClient(_ => HealthyCall()), new PassthroughModelRuntime(), [.. tool.GetTools()],
            traces, new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 3 });

        var result = await harness.RunAsync(new HarnessRunRequest(null, "同一调用反复来"));

        Assert.Equal(AgentRunStatus.LoopDetected, result.Status);
        Assert.Equal(AgentRunCode.LoopDetected, result.Code);
        Assert.Equal(2, tool.Calls);   // 第 3 次相同调用在真正执行前就被拦下，没白花钱

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.LoopDetected, trace.Status);
        Assert.Equal(AgentRunCode.LoopDetected, trace.ErrorCode);
        Assert.Equal(3, trace.Steps.Count);   // 被拦的那次也留轨迹：为什么停要能回放
    }

    [Fact]
    public async Task SameScript_WithLoopDetectionDisabled_RunsToIterationLimit()
    {
        // 负控：同一份脚本，只把重复检测关掉（0）—— 结局必须从 loop_detected 变成 iteration_limit。
        var tool = new FaultInjectorTool();
        var harness = Build(
            new ScriptedChatClient(_ => HealthyCall()), new PassthroughModelRuntime(), [.. tool.GetTools()],
            new RecordingTraceStore(),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        var result = await harness.RunAsync(new HarnessRunRequest(null, "同一调用反复来"));

        Assert.Equal(AgentRunStatus.IterationLimit, result.Status);
        Assert.Equal(10, tool.Calls);
    }

    [Fact]
    public async Task TokenBudgetExceeded_StopsBeforeBurningMore()
    {
        var tool = new FaultInjectorTool();
        var traces = new RecordingTraceStore();
        // 每次模型请求烧 100 token，预算 150：第一轮还有余量，第二轮之前就必须停。
        var harness = Build(
            new ScriptedChatClient(_ => HealthyCall()), new MeteredModelRuntime { TokensPerRequest = 100 },
            [.. tool.GetTools()], traces,
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxTokensPerRun = 150 });

        var result = await harness.RunAsync(new HarnessRunRequest(null, "烧钱"));

        Assert.Equal(AgentRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(AgentRunCode.BudgetExceeded, result.Code);
        Assert.Equal(1, tool.Calls);   // 第二次调用之前预算已破，没有继续烧

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.BudgetExceeded, trace.Status);
        Assert.Equal(AgentRunCode.BudgetExceeded, trace.ErrorCode);
    }

    [Fact]
    public async Task SameScript_WithTokenBudgetDisabled_RunsToIterationLimit()
    {
        // 负控：同一份脚本、同样烧钱，只把预算关掉（0）—— 结局变成 iteration_limit。
        var tool = new FaultInjectorTool();
        var harness = Build(
            new ScriptedChatClient(_ => HealthyCall()), new MeteredModelRuntime { TokensPerRequest = 100 },
            [.. tool.GetTools()], new RecordingTraceStore(),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxTokensPerRun = 0, MaxRepeatedToolCalls = 0 });

        var result = await harness.RunAsync(new HarnessRunRequest(null, "烧钱"));

        Assert.Equal(AgentRunStatus.IterationLimit, result.Status);
        Assert.Equal(10, tool.Calls);
    }

    [Fact]
    public async Task ReachingIterationBudget_IsIterationLimit_NotSucceeded()
    {
        var tool = new FaultInjectorTool();
        var harness = Build(
            new ScriptedChatClient(_ => HealthyCall()), new PassthroughModelRuntime(), [.. tool.GetTools()],
            new RecordingTraceStore(),
            new AgentLoopOptions { MaxIterations = 2, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        var result = await harness.RunAsync(new HarnessRunRequest(null, "每轮都要工具"));

        // 框架跑满迭代不报错，而是清空工具逼模型收口：不主动分类就会记成 succeeded。
        Assert.Equal(AgentRunStatus.IterationLimit, result.Status);
        Assert.Equal(AgentRunCode.IterationLimit, result.Code);
    }

    [Fact]
    public async Task ModelStopsWithText_IsSucceeded_NotIterationLimit()
    {
        // 对照：同一形状，第 2 轮改成给文本 —— 正常收尾必须是 succeeded 且没有归类码。
        var tool = new FaultInjectorTool();
        var harness = Build(
            new ScriptedChatClient(call => call == 1 ? HealthyCall() : ScriptedResponses.Text("done")),
            new PassthroughModelRuntime(), [.. tool.GetTools()], new RecordingTraceStore(),
            new AgentLoopOptions { MaxIterations = 2, TimeoutSeconds = 30 });

        var result = await harness.RunAsync(new HarnessRunRequest(null, "会答完"));

        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Null(result.Code);
        Assert.Equal("done", result.Text);
    }

    [Fact]
    public async Task CallerCancellation_PersistsCancelled_NotFailed()
    {
        var tool = new FaultInjectorTool();
        var traces = new RecordingTraceStore();
        var harness = Build(
            new ScriptedChatClient(_ => SlowCall()), new PassthroughModelRuntime(), [.. tool.GetTools()],
            traces, new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 60 });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunAsync(new HarnessRunRequest(null, "中途取消"), cts.Token));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Cancelled, trace.Status);
        Assert.Equal(AgentRunCode.Cancelled, trace.ErrorCode);
        Assert.True(tool.ObservedCancellation);   // 取消真的穿进了工具内部
    }

    [Fact]
    public async Task SameTool_WithoutCancellation_CompletesNormally()
    {
        // 对照：同一形状的慢工具，只把等待拧短且不取消 —— 结局是 succeeded，不是 cancelled。
        var tool = new FaultInjectorTool();
        var harness = Build(
            new ScriptedChatClient(call => call == 1 ? SlowCall(delayMs: 1) : ScriptedResponses.Text("done")),
            new PassthroughModelRuntime(), [.. tool.GetTools()], new RecordingTraceStore(),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 60 });

        var result = await harness.RunAsync(new HarnessRunRequest(null, "慢但没取消"));

        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
        Assert.Null(result.Code);
        Assert.False(tool.ObservedCancellation);
    }

    [Fact]
    public async Task ContinuousToolFailures_AreClassifiedAsToolError()
    {
        var tool = new FaultInjectorTool();
        var traces = new RecordingTraceStore();
        var harness = Build(
            new ScriptedChatClient(_ => ThrowCall(failTimes: 99)), new PassthroughModelRuntime(),
            [.. tool.GetTools()], traces,
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        await Assert.ThrowsAnyAsync<Exception>(
            () => harness.RunAsync(new HarnessRunRequest(null, "工具连续失败")));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Failed, trace.Status);
        Assert.Equal(AgentRunCode.ToolError, trace.ErrorCode);   // 不是一坨字符串，是可判定的码
    }

    [Fact]
    public async Task RunWallClockTimeout_IsTimeoutWithRunTimeoutCode()
    {
        var tool = new FaultInjectorTool();
        var traces = new RecordingTraceStore();
        var harness = Build(
            new ScriptedChatClient(_ => SlowCall()), new PassthroughModelRuntime(), [.. tool.GetTools()],
            traces, new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 1 });

        await Assert.ThrowsAsync<AgentRunTimeoutException>(
            () => harness.RunAsync(new HarnessRunRequest(null, "跑太久")));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Timeout, trace.Status);
        Assert.Equal(AgentRunCode.RunTimeout, trace.ErrorCode);
    }

    [Fact]
    public async Task ModelNotConfigured_PersistsModelUnavailableCode()
    {
        var traces = new RecordingTraceStore();
        var factory = new Mock<IChatClientFactory>(MockBehavior.Strict);
        factory.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatEndpoint?)null);
        var harness = Build(factory.Object, new PassthroughModelRuntime(), [], traces,
            new AgentLoopOptions { MaxIterations = 3, TimeoutSeconds = 30 });

        await Assert.ThrowsAsync<HarnessModelNotConfiguredException>(
            () => harness.RunAsync(new HarnessRunRequest(null, "没模型")));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Failed, trace.Status);
        Assert.Equal(AgentRunCode.ModelUnavailable, trace.ErrorCode);
    }

    [Fact]
    public async Task GetRunAsync_SurfacesStatusAndCode()
    {
        var tool = new FaultInjectorTool();
        var traces = new RecordingTraceStore();
        var harness = Build(
            new ScriptedChatClient(_ => HealthyCall()), new PassthroughModelRuntime(), [.. tool.GetTools()],
            traces, new AgentLoopOptions { MaxIterations = 4, TimeoutSeconds = 30, MaxRepeatedToolCalls = 3 });

        var result = await harness.RunAsync(new HarnessRunRequest(null, "回放"));
        var replay = await harness.GetRunAsync(result.RunId);

        Assert.NotNull(replay);
        Assert.Equal(result.Status, replay.Status);
        Assert.Equal(result.Code, replay.Code);
        // 步序列也必须逐项复原（原子 AgentTask 的 GetRunAsync_RoundtripsStatusCodeSteps，按分层职责归此层）
        Assert.Equal(result.Steps.Count, replay.Steps.Count);
        for (var i = 0; i < result.Steps.Count; i++)
        {
            Assert.Equal(result.Steps[i].Kind, replay.Steps[i].Kind);
            Assert.Equal(result.Steps[i].Tool, replay.Steps[i].Tool);
        }
    }

    private static ChatResponse HealthyCall()
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", "throw"), ("failTimes", 0), ("delayMs", 0)));

    private static ChatResponse ThrowCall(int failTimes)
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", "throw"), ("failTimes", failTimes), ("delayMs", 0)));

    private static ChatResponse SlowCall(int delayMs = 60_000)
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", "hang"), ("failTimes", 0), ("delayMs", delayMs)));

    private static IAgentHarness Build(
        IChatClient inner, IModelRuntime runtime, IReadOnlyList<AITool> tools,
        IExecutionTraceStore traces, AgentLoopOptions loopOptions)
    {
        var factory = new Mock<IChatClientFactory>(MockBehavior.Strict);
        var endpoint = new ChatEndpoint(1, "http://test", "key", "model");
        factory.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(endpoint);
        factory.Setup(f => f.GetClient(It.IsAny<ChatEndpoint>())).Returns(inner);
        return Build(factory.Object, runtime, tools, traces, loopOptions);
    }

    private static IAgentHarness Build(
        IChatClientFactory factory, IModelRuntime runtime, IReadOnlyList<AITool> tools,
        IExecutionTraceStore traces, AgentLoopOptions loopOptions)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new AgentHarnessService(
            factory,
            runtime,
            new FakeToolCatalog(tools),
            new ToolExecutionPipeline(
                Options.Create(new ToolExecutionOptions()), AutoToolApprovalPolicy.Instance, NullLoggerFactory.Instance, services),
            Options.Create(new ContextOptions { Enabled = false }),
            new SessionStore(Options.Create(new MemoryOptions()), TimeProvider.System),
            Options.Create(new MemoryOptions()),
            new AsyncLocalMemoryScopeAccessor(),
            new AgentLoopService(Options.Create(loopOptions), NullLogger<AgentLoopService>.Instance),
            traces,
            NullLoggerFactory.Instance,
            services,
            NullLogger<AgentHarnessService>.Instance);
    }

    /// <summary>按请求累计用量的模型运行时替身：让 token 预算这条路径可以在单测里被真实走到。</summary>
    private sealed class MeteredModelRuntime : IModelRuntime
    {
        private long _requests;
        private long _tokens;

        public long TokensPerRequest { get; set; }

        public IChatClient Wrap(IChatClient innerClient) => new MeteringChatClient(innerClient, this);

        public ModelUsageSnapshot GetUsage()
            => new(Interlocked.Read(ref _requests), Interlocked.Read(ref _tokens), 0);

        private void Record()
        {
            Interlocked.Increment(ref _requests);
            Interlocked.Add(ref _tokens, TokensPerRequest);
        }

        private sealed class MeteringChatClient(IChatClient inner, MeteredModelRuntime owner) : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                var response = await inner.GetResponseAsync(messages, options, cancellationToken);
                owner.Record();
                return response;
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                CancellationToken cancellationToken = default)
                => throw new NotSupportedException("harness 单测只走非流式路径");

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose() { }
        }
    }

    private sealed class RecordingTraceStore : IExecutionTraceStore
    {
        public List<ExecutionTrace> Saved { get; } = [];

        public Task<long> SaveAsync(ExecutionTrace trace, CancellationToken cancellationToken = default)
        {
            Saved.Add(trace);
            return Task.FromResult((long)Saved.Count);
        }

        public Task<ExecutionTrace?> GetAsync(long runId, CancellationToken cancellationToken = default)
            => Task.FromResult(runId >= 1 && runId <= Saved.Count ? Saved[(int)runId - 1] : null);

        public Task<IReadOnlyList<ExecutionTrace>> QueryAsync(
            ExecutionTraceQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionTrace>>(Saved
                .Where(t => query.SessionId is null || t.SessionId == query.SessionId)
                .Where(t => query.Status is null || t.Status == query.Status)
                .Reverse()
                .Take(query.Limit > 0 ? query.Limit : 50)
                .ToList());
    }
}
