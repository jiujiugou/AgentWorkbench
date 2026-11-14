using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkbench.Observability;
using AgentWorkbench.Runtime.Context;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Runtime.Loop;
using AgentWorkbench.Runtime.Model;
using AgentWorkbench.Runtime.Session;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Shared;
using AgentWorkbench.TestKit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.CoreTests.Runtime;

/// <summary>
/// ② 循环驱动的 Harness 级回归锁（对应 docs/core/循环驱动.md 第六节）。
///
/// 定位：验「一次真 run 里循环到底怎么转」，不是验装配形状（① 的 请求装配.cs 已经验了
/// 「没包第二层」），也不是验工具层回填（④ 的 ToolCallGapTests）。
/// 循环本体由 MEA 的 FunctionInvokingChatClient 提供，本仓不自研——这里锁的是
/// 「装配是自己的、升级框架后不能被悄悄改坏」。
///
/// 每条都写明负控：改坏什么会红。能脚本判定的禁止用 AI 判。
/// </summary>
public sealed class 循环驱动
{
    // ── 替身 ────────────────────────────────────────────────────────

    /// <summary>只实现写口：Harness 现在只依赖 IExecutionTraceWriter。</summary>
    private sealed class RecordingTraces : IExecutionTraceWriter
    {
        public List<ExecutionTrace> Saved { get; } = [];

        public Task<long> SaveAsync(ExecutionTrace trace, CancellationToken cancellationToken = default)
        {
            Saved.Add(trace);
            return Task.FromResult((long)Saved.Count);
        }
    }

    private sealed class FixedFactory(IChatClient client) : IChatClientFactory
    {
        public Task<ChatEndpoint?> ResolveAsync(string? modelOverride, CancellationToken cancellationToken)
            => Task.FromResult<ChatEndpoint?>(new ChatEndpoint(null, "http://localhost", "k", "test-model"));

        public IChatClient GetClient(ChatEndpoint endpoint) => client;
    }

    private sealed class PassthroughRuntime : IModelRuntime
    {
        public IChatClient Wrap(IChatClient innerClient) => innerClient;

        public ModelUsageSnapshot GetUsage() => new(0, 0, 0);
    }

    private sealed class FixedCatalog(params AITool[] tools) : IToolCatalog
    {
        public IReadOnlyList<AITool> GetTools() => tools;

        public IReadOnlyList<string> GetToolNames() => tools.Select(t => t.Name).ToList();
    }

    private sealed class AllowAll : IToolApprovalPolicy
    {
        public ToolApprovalMode Decision(string toolName) => ToolApprovalMode.Auto;
    }

    /// <summary>计数工具：验「真的执行了几次」，不是「轨迹里写了几条」。</summary>
    private sealed class CountingEcho
    {
        public int Calls { get; private set; }

        public string Echo(string text)
        {
            Calls++;
            return "echo:" + text;
        }
    }

    /// <summary>一轮里同时返回两个工具调用（批次）。</summary>
    private sealed class BatchChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount > 1)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "都完成了")));

            AIContent[] contents =
            [
                new FunctionCallContent("c1", "Echo", new Dictionary<string, object?> { ["text"] = "a" }),
                new FunctionCallContent("c2", "Echo", new Dictionary<string, object?> { ["text"] = "b" })
            ];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>流式一次答复拆成多片：验 Harness 的 StreamRunAsync 不丢分片。</summary>
    private sealed class ChunkedStreamClient(string[] chunks) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(chunks))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            foreach (var chunk in chunks)
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    /// <summary>每轮都想调工具，且记录每轮请求里还剩几个工具（验迭代上限的落地）。</summary>
    private sealed class AlwaysToolClient : IChatClient
    {
        public List<int> ToolCounts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ToolCounts.Add(options?.Tools?.Count ?? 0);
            var call = new FunctionCallContent(
                "c1", "Echo", new Dictionary<string, object?> { ["text"] = "again" });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    // ── 最小装配：真 Harness + 真 ToolExecutionPipeline + 假模型 ────────

    private static (IAgentHarness Harness, RecordingTraces Traces) Build(
        IChatClient client,
        CountingEcho tool,
        AgentLoopOptions? loopOptions = null,
        int maxResultChars = 400)
    {
        var traces = new RecordingTraces();
        var memory = new MemoryOptions { SessionEnabled = false };
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        var contextServices = new ServiceCollection();
        contextServices.AddLogging();
        contextServices.AddNitroContext(new ConfigurationBuilder().Build());
        var context = contextServices.BuildServiceProvider().GetRequiredService<IRequestContextComposer>();

        var pipeline = new ToolExecutionPipeline(
            Options.Create(new ToolExecutionOptions { MaxResultChars = maxResultChars }),
            new AllowAll(), NullLoggerFactory.Instance, services);

        var harness = new AgentHarnessService(
            new FixedFactory(client),
            new PassthroughRuntime(),
            new FixedCatalog(AIFunctionFactory.Create(tool.Echo, "Echo")),
            pipeline,
            context,
            new SessionStore(Options.Create(memory), TimeProvider.System),
            new SessionRunGate(),
            Options.Create(memory),
            new AsyncLocalMemoryScopeAccessor(),
            new AgentLoopService(
                Options.Create(loopOptions ?? new AgentLoopOptions
                {
                    MaxIterations = 6, TimeoutSeconds = 5, MaxRepeatedToolCalls = 0
                }),
                NullLogger<AgentLoopService>.Instance),
            traces,
            NullLoggerFactory.Instance,
            services,
            NullLogger<AgentHarnessService>.Instance);

        return (harness, traces);
    }

    // ── 四条回归锁：回填 / 批次 / 自然收尾 / 流式分片 ─────────────────

    // 负控：把 UseProvidedChatClientAsIs 去掉（MAF 另包一层环）→ 工具不会经我们的管道，Calls 归零，本条红。
    [Fact]
    public async Task 两轮不同工具的回填_模型被再问一次且两轮都进轨迹()
    {
        var llm = new ScriptedChatClient()
            .ToolCall("Echo", new Dictionary<string, object?> { ["text"] = "第一轮" })
            .ToolCall("Echo", new Dictionary<string, object?> { ["text"] = "第二轮" })
            .Reply("收口");
        var tool = new CountingEcho();
        var (harness, traces) = Build(llm, tool);

        await harness.RunAsync(new HarnessRunRequest("c1", "请连做两轮"));

        Assert.Equal(3, llm.CallCount);          // 工具轮 + 回填后再问一轮 + 收口轮
        Assert.Equal(2, tool.Calls);             // 两个工具调用真的执行了
        var trace = Assert.Single(traces.Saved);
        Assert.Equal(2, trace.Steps.Count(s => s.Kind == "tool"));
        Assert.Equal(AgentRunStatus.Succeeded, trace.Status);
        Assert.Contains(trace.Steps, s => s.Kind == "assistant" && s.Result == "收口");
    }

    // 负控：FICC 改成一次只执行批次里的第一个 → Calls 变 1，本条红。
    [Fact]
    public async Task 一轮里两个工具调用_都执行且都留痕()
    {
        var llm = new BatchChatClient();
        var tool = new CountingEcho();
        var (harness, traces) = Build(llm, tool);

        await harness.RunAsync(new HarnessRunRequest("c2", "并行做两件"));

        Assert.Equal(2, llm.CallCount);          // 工具轮 + 收口轮
        Assert.Equal(2, tool.Calls);             // 同一轮里两个调用都执行
        var trace = Assert.Single(traces.Saved);
        Assert.Equal(2, trace.Steps.Count(s => s.Kind == "tool"));
        Assert.All(trace.Steps.Where(s => s.Kind == "tool"), s => Assert.Equal(0, s.Iteration));
    }

    // 负控：循环结束后又多问模型一次 → CallCount 变 2，本条红。
    [Fact]
    public async Task 模型不再调工具时自然收尾_不多问一次()
    {
        var llm = new ScriptedChatClient().Reply("直接答完");
        var tool = new CountingEcho();
        var (harness, traces) = Build(llm, tool);

        await harness.RunAsync(new HarnessRunRequest("c3", "不用工具"));

        Assert.Equal(1, llm.CallCount);
        Assert.Equal(0, tool.Calls);
        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Succeeded, trace.Status);
        Assert.Single(trace.Steps, s => s.Kind == "assistant");
    }

    // 负控：StreamRunAsync 里只取第一个 update.Text 就 break → 文本拼不全，本条红。
    [Fact]
    public async Task 流式文本分片不丢_拼成完整答复()
    {
        string[] chunks = ["第一片", "第二片", "第三片"];
        var llm = new ChunkedStreamClient(chunks);
        var tool = new CountingEcho();
        var (harness, traces) = Build(llm, tool);

        var deltas = new List<HarnessStreamEvent>();
        await foreach (var evt in harness.StreamAsync(new HarnessRunRequest("c4", "流式")))
            if (evt.Kind == HarnessStreamEventKind.TextDelta)
                deltas.Add(evt);

        Assert.Equal(chunks.Length, deltas.Count);
        Assert.Equal(string.Concat(chunks), string.Concat(deltas.Select(d => d.Text)));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(string.Concat(chunks),
            trace.Steps.Single(s => s.Kind == "assistant").Result);
    }

    // ── 迭代上限与两条出口 ──────────────────────────────────────────

    // 负控：ToolExecutionPipeline 不把 limits.MaxIterations 赋给 MaximumIterationsPerRequest
    //       → 工具执行次数会超过上限，本条红。这是把配置与循环本体连起来的唯一断言。
    [Fact]
    public async Task MaximumIterations落地_工具执行次数不超过上限()
    {
        var llm = new AlwaysToolClient();
        var tool = new CountingEcho();
        var (harness, traces) = Build(
            llm, tool,
            loopOptions: new AgentLoopOptions
            {
                MaxIterations = 2, TimeoutSeconds = 5, MaxRepeatedToolCalls = 0
            });

        await harness.RunAsync(new HarnessRunRequest("c5", "一直调工具"));

        Assert.NotEmpty(llm.ToolCounts);         // 扫描器自证：模型真的被问过，不是一次都没跑
        Assert.Equal(2, tool.Calls);             // 恰好跑满上限，不是「没跑所以没超」
        var trace = Assert.Single(traces.Saved);
        Assert.Equal(2, trace.Steps.Count(s => s.Kind == "tool"));
    }

    // 负控：把 StreamAsync 改成另一套循环（不共用 _loop / guard）→ 两条出口的步骤数或终态不一致，本条红。
    [Fact]
    public async Task 同步与流式两条出口_循环次数与终态一致()
    {
        static ScriptedChatClient Llm() => new ScriptedChatClient()
            .ToolCall("Echo", new Dictionary<string, object?> { ["text"] = "hi" })
            .Reply("都完成了");

        var (syncHarness, syncTraces) = Build(Llm(), new CountingEcho());
        var (streamHarness, streamTraces) = Build(Llm(), new CountingEcho());

        var syncResult = await syncHarness.RunAsync(new HarnessRunRequest("c6a", "同步"));

        var events = new List<HarnessStreamEvent>();
        await foreach (var evt in streamHarness.StreamAsync(new HarnessRunRequest("c6b", "流式")))
            events.Add(evt);

        var sync = Assert.Single(syncTraces.Saved);
        var stream = Assert.Single(streamTraces.Saved);

        Assert.Equal(sync.Status, stream.Status);
        Assert.True(sync.Steps.Count >= 1, "两条出口都没跑出工具步，相等是空的相等");
        Assert.Equal(sync.Steps.Count, stream.Steps.Count);
        Assert.Equal(sync.Iterations, stream.Iterations);
        Assert.Equal(syncResult.Text,
            events.Single(e => e.Kind == HarnessStreamEventKind.Completed).Result!.Text);
    }

    // 负控：把 PersistQuietlyAsync 的 CancellationToken.None 换成调用方 token，
    //       或把取消路径的 complete: false 去掉 → 本条红。
    [Fact]
    public async Task 中途取消_循环停下且记录声明不完整()
    {
        var llm = new ScriptedChatClient().Delay(TimeSpan.FromSeconds(5), "不该到达");
        var tool = new CountingEcho();
        var (harness, traces) = Build(llm, tool);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.RunAsync(new HarnessRunRequest("c7", "慢请求"), cts.Token));

        Assert.Equal(1, llm.CallCount);          // 循环没有再问模型第二轮
        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Cancelled, trace.Status);
        Assert.False(trace.Complete);
    }

    // 负控：把 TerminateOnUnknownCalls 改成 true（或升级 MEA 改了默认值）
    //       → 整轮会以异常/拒绝结束，本条红。这是「依赖框架默认值」的行为锁。
    [Fact]
    public async Task 未知工具被回填而不是终止_框架默认行为回归锁()
    {
        var llm = new ScriptedChatClient()
            .ToolCall("NotInCatalog", callId: "unknown-1")
            .Reply("模型收到回填后收口");
        var tool = new CountingEcho();
        var (harness, traces) = Build(llm, tool);

        await harness.RunAsync(new HarnessRunRequest("c8", "请调一个不存在的工具"));

        Assert.Equal(0, tool.Calls);             // 没有执行任何真实工具
        Assert.Equal(2, llm.CallCount);          // 回填后模型又被问了一次
        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Succeeded, trace.Status);
    }
}
