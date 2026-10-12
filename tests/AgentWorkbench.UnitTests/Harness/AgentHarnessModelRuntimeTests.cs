using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.Context;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Memory;
using AgentWorkbench.ModelRuntime;
using AgentWorkbench.Observability;
using AgentWorkbench.Providers;
using AgentWorkbench.ToolExecution;
using AgentWorkbench.UnitTests.Conversation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.UnitTests.Harness;

/// <summary>
/// 判定型：Harness 的模型请求必须走 ModelRuntime（超时 / 重试 / 用量），
/// 否则「模型交互」这套策略对编排入口（Harness / Workflow）就是失效的。
///
/// 负控（必须能红）：把 <c>AgentHarnessService.RunAsync</c> 里的 <c>_runtime.Wrap(...)</c> 去掉，
/// 首次请求的瞬时失败不再重试、用量也不再计数 —— 本用例的两个断言同时失败。
/// </summary>
public sealed class AgentHarnessModelRuntimeTests
{
    [Fact]
    public async Task RunAsync_RoutesModelRequestsThroughModelRuntime()
    {
        var attempts = 0;
        var client = new CountingChatClient(_ =>
        {
            attempts++;
            return attempts == 1
                ? Task.FromException<ChatResponse>(new HttpRequestException("boom"))
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
        });

        var (harness, runtime, traces) = Build(
            client, new ModelRuntimeOptions { MaxRetries = 1, RetryBaseDelayMilliseconds = 1 });

        var result = await harness.RunAsync(new HarnessRunRequest(null, "hi", UseKnowledge: false));

        Assert.Equal("done", result.Text);
        Assert.Equal(2, attempts);                      // 第 1 次瞬时失败 + 第 2 次成功
        Assert.Equal(2, runtime.GetUsage().Requests);   // ModelRuntime 记到两次「实际发出的请求」
        Assert.Single(traces.Saved);
        Assert.Equal(ExecutionTrace.Succeeded, traces.Saved[0].Status);
    }

    private static (IAgentHarness Harness, IModelRuntime Runtime, FakeTraceStore Traces) Build(
        IChatClient inner, ModelRuntimeOptions runtimeOptions)
    {
        var factory = new Mock<IChatClientFactory>(MockBehavior.Strict);
        var endpoint = new ChatEndpoint(1, "http://test", "key", "model");
        factory.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(endpoint);
        factory.Setup(f => f.GetClient(It.IsAny<ChatEndpoint>())).Returns(inner);

        var runtime = new ModelRuntimeService(
            Options.Create(runtimeOptions),
            new ModelUsageMeter(),
            NullLoggerFactory.Instance,
            NullLogger<ModelRuntimeService>.Instance);

        var services = new ServiceCollection().BuildServiceProvider();
        var traces = new FakeTraceStore();

        var harness = new AgentHarnessService(
            factory.Object,
            runtime,
            new FakeToolCatalog(),
            new ToolExecutionPipeline(
                Options.Create(new ToolExecutionOptions()), NullLoggerFactory.Instance, services),
            new ContextBuilder(),
            Options.Create(new ContextOptions { Enabled = false }),
            new SessionStore(Options.Create(new MemoryOptions()), TimeProvider.System),
            Options.Create(new MemoryOptions()),
            new AsyncLocalMemoryScopeAccessor(),
            new AgentLoopService(
                Options.Create(new AgentLoopOptions { MaxIterations = 3, TimeoutSeconds = 30 }),
                NullLogger<AgentLoopService>.Instance),
            traces,
            Mock.Of<IKnowledgeEngine>(),
            NullLoggerFactory.Instance,
            services,
            NullLogger<AgentHarnessService>.Instance);

        return (harness, runtime, traces);
    }

    private sealed class FakeTraceStore : IExecutionTraceStore
    {
        public List<ExecutionTrace> Saved { get; } = [];

        public Task<long> SaveAsync(ExecutionTrace trace, CancellationToken cancellationToken = default)
        {
            Saved.Add(trace);
            return Task.FromResult((long)Saved.Count);
        }

        public Task<ExecutionTrace?> GetAsync(long runId, CancellationToken cancellationToken = default)
            => Task.FromResult<ExecutionTrace?>(null);
    }

    private sealed class CountingChatClient(Func<CancellationToken, Task<ChatResponse>> handler) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => handler(cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("harness 单测只走非流式路径");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
