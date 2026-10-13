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
/// 探针（端到端到落库那一层）：工具连续失败时，Harness 必须把整轮 run 记成 failed，
/// 且失败原因跟着每一步一起落库 —— 否则回放时分不清「工具炸了」和「工具返回了空」。
/// </summary>
public sealed class HarnessToolFailureProbeTests
{
    [Fact]
    public async Task ContinuousToolFailures_PersistFailedRun_WithPerStepReasons()
    {
        var tool = new FaultInjectorTool();
        var client = new ScriptedChatClient(_ => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", "throw"), ("failTimes", 99), ("delayMs", 0))));
        var traces = new RecordingTraceStore();
        var harness = Build(client, tool, traces);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await harness.RunAsync(new HarnessRunRequest(null, "按脚本调用坏工具")));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(ExecutionTrace.Failed, trace.Status);
        Assert.NotNull(trace.Error);

        var toolSteps = trace.Steps.Where(step => step.Kind == "tool").ToList();
        Assert.NotEmpty(toolSteps);
        Assert.All(toolSteps, step => Assert.NotNull(step.Error));
    }

    [Fact]
    public async Task HealthyTool_PersistsSucceededRun_WithoutReasons()
    {
        var tool = new FaultInjectorTool();
        // 同一形状，只把 failTimes 拧到 0：run 正常收尾，落库状态应是 succeeded。
        var client = new ScriptedChatClient(call => call == 1
            ? ScriptedResponses.ToolCall(
                FaultInjectorTool.ToolName,
                ScriptedResponses.Args(("mode", "throw"), ("failTimes", 0), ("delayMs", 0)))
            : ScriptedResponses.Text("done"));
        var traces = new RecordingTraceStore();
        var harness = Build(client, tool, traces);

        var result = await harness.RunAsync(new HarnessRunRequest(null, "按脚本调用健康工具"));

        Assert.Equal("done", result.Text);
        var trace = Assert.Single(traces.Saved);
        Assert.Equal(ExecutionTrace.Succeeded, trace.Status);
        Assert.All(trace.Steps, step => Assert.Null(step.Error));
    }

    private static IAgentHarness Build(IChatClient inner, FaultInjectorTool tool, IExecutionTraceStore traces)
    {
        var factory = new Mock<IChatClientFactory>(MockBehavior.Strict);
        var endpoint = new ChatEndpoint(1, "http://test", "key", "model");
        factory.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(endpoint);
        factory.Setup(f => f.GetClient(It.IsAny<ChatEndpoint>())).Returns(inner);

        var services = new ServiceCollection().BuildServiceProvider();

        return new AgentHarnessService(
            factory.Object,
            new PassthroughModelRuntime(),
            new FakeToolCatalog([.. tool.GetTools()]),
            new ToolExecutionPipeline(
                Options.Create(new ToolExecutionOptions()), AutoToolApprovalPolicy.Instance, NullLoggerFactory.Instance, services),
            Options.Create(new ContextOptions { Enabled = false }),
            new SessionStore(Options.Create(new MemoryOptions()), TimeProvider.System),
            Options.Create(new MemoryOptions()),
            new AsyncLocalMemoryScopeAccessor(),
            new AgentLoopService(
                Options.Create(new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 }),
                NullLogger<AgentLoopService>.Instance),
            traces,
            NullLoggerFactory.Instance,
            services,
            NullLogger<AgentHarnessService>.Instance);
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
            => Task.FromResult<ExecutionTrace?>(null);

        public Task<IReadOnlyList<ExecutionTrace>> QueryAsync(
            ExecutionTraceQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionTrace>>([]);
    }
}
