using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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

namespace AgentWorkbench.CoreTests.Engineering.Observability;

/// <summary>
/// ⑥ 轨迹与观测的内核侧判据（对应 docs/core/轨迹与观测.md 第一、二、五、六节）。
///
/// 定位：验的是「内核交给存储的那条记录字段对不对」，不是「存储写对了」。
/// 落库往返、检索面、报告全属应用层（部件文档第六节已划出），这里一条都不碰。
///
/// 当前状态：本文件里**有一部分是预期红**——红的每一条都对应
/// agent-parts.toml 里 status = "partial" 的那条能力。红是正确信号，不要改测试迁就生产。
/// 每条都写明负控：改坏什么会红。能脚本判定的禁止用 AI 判。
/// </summary>
public sealed class 轨迹与观测
{
    // ── 替身（只为让内核跑起来；不模拟业务）──────────────────────────

    /// <summary>把每次落库的轨迹与收到的取消令牌都记下来——断言的就是「内核交出了什么」。</summary>
    private sealed class RecordingTraces : IExecutionTraceStore
    {
        public List<ExecutionTrace> Saved { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public Task<long> SaveAsync(ExecutionTrace trace, CancellationToken cancellationToken = default)
        {
            Saved.Add(trace);
            Tokens.Add(cancellationToken);
            return Task.FromResult((long)Saved.Count);
        }

        public Task<ExecutionTrace?> GetAsync(long runId, CancellationToken cancellationToken = default)
            => Task.FromResult<ExecutionTrace?>(null);

        public Task<IReadOnlyList<ExecutionTrace>> QueryAsync(
            ExecutionTraceQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionTrace>>([]);
    }

    /// <summary>端点固定，客户端固定。</summary>
    private sealed class FixedFactory(IChatClient client) : IChatClientFactory
    {
        public Task<ChatEndpoint?> ResolveAsync(string? modelOverride, CancellationToken cancellationToken)
            => Task.FromResult<ChatEndpoint?>(new ChatEndpoint(null, "http://localhost", "test-key", "test-model"));

        public IChatClient GetClient(ChatEndpoint endpoint) => client;
    }

    /// <summary>
    /// 用量来源故意报一个天文数字：若实现改成读进程累计，断言必然落空。
    /// 这就是「本次 run 的用量」与「进程累计」两条口径的分离点。
    /// </summary>
    private sealed class SkewedRuntime : IModelRuntime
    {
        public IChatClient Wrap(IChatClient innerClient) => innerClient;

        public ModelUsageSnapshot GetUsage() => new(999, 999_000, 999_000);
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

    /// <summary>准备阶段就失败的会话存储：验「准备阶段抛异常是否也留痕」。</summary>
    private sealed class ThrowingStore : ISessionStore
    {
        public Task<AgentSession> GetOrCreateAsync(
            string sessionId, ISessionFactory factory, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("取会话失败");

        public Task SaveAsync(
            string sessionId, ISessionFactory factory, AgentSession session,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>给每次模型响应挂上用量：验「记录里的 token 取自响应对象」。</summary>
    private sealed class UsageChatClient(IChatClient inner, long input, long output) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await inner.GetResponseAsync(messages, options, cancellationToken);
            response.Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = output };
            return response;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => inner.GetService(serviceType, serviceKey);

        public void Dispose() => inner.Dispose();
    }

    /// <summary>模拟 provider 在流末尾报用量（MEA 约定：最后一个 update 带 <c>UsageContent</c>）。</summary>
    private sealed class UsageStreamClient(IChatClient inner, long input, long output) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => inner.GetResponseAsync(messages, options, cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken))
                yield return update;

            yield return new ChatResponseUpdate(ChatRole.Assistant,
                [new UsageContent(new UsageDetails { InputTokenCount = input, OutputTokenCount = output })]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => inner.GetService(serviceType, serviceKey);

        public void Dispose() => inner.Dispose();
    }

    /// <summary>可变的用量来源：让「已用多少」与「上限多少」是两个不同的数。</summary>
    private sealed class FakeUsage(AgentUsage initial) : IAgentUsageSource
    {
        public AgentUsage Current { get; set; } = initial;

        public AgentUsage GetUsage() => Current;
    }

    private sealed class EchoTool
    {
        public string Echo(string text) => "echo:" + text;
    }

    private sealed class LongTool
    {
        public string Long() => new('x', 500);
    }

    // ── 最小装配：真 Harness + 真工具环 + 假存储 ─────────────────────

    private static (IAgentHarness Harness, RecordingTraces Traces) Build(
        ScriptedChatClient llm,
        IReadOnlyList<AITool>? tools = null,
        ISessionStore? sessions = null,
        MemoryOptions? memory = null,
        AgentLoopOptions? loopOptions = null,
        int maxResultChars = 400,
        IChatClient? clientSeenByHarness = null)
    {
        var traces = new RecordingTraces();
        var memoryOptions = memory ?? new MemoryOptions { SessionEnabled = false };
        var toolList = tools ?? [];
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        // 装配器是 internal，只能经 DI 取（与 S03 同一条路径）。
        var contextServices = new ServiceCollection();
        contextServices.AddLogging();
        contextServices.AddNitroContext(new ConfigurationBuilder().Build());
        var context = contextServices.BuildServiceProvider().GetRequiredService<IRequestContextComposer>();

        var pipeline = new ToolExecutionPipeline(
            Options.Create(new ToolExecutionOptions { MaxResultChars = maxResultChars }),
            new AllowAll(), NullLoggerFactory.Instance, services);

        var harness = new AgentHarnessService(
            new FixedFactory(clientSeenByHarness ?? llm),
            new SkewedRuntime(),
            new FixedCatalog([.. toolList]),
            pipeline,
            context,
            sessions ?? new SessionStore(Options.Create(memoryOptions), TimeProvider.System),
            new SessionRunGate(),
            Options.Create(memoryOptions),
            new AsyncLocalMemoryScopeAccessor(),
            new AgentLoopService(
                Options.Create(loopOptions ?? new AgentLoopOptions { TimeoutSeconds = 5 }),
                NullLogger<AgentLoopService>.Instance),
            traces,
            NullLoggerFactory.Instance,
            services,
            NullLogger<AgentHarnessService>.Instance);

        return (harness, traces);
    }

    // ── 结局与归类码 ────────────────────────────────────────────────

    // 负控：把成功路径的 status 改成 failed、或给它硬塞一个归类码 → 本条红。
    [Fact]
    public async Task 成功收尾的记录里是succeeded且没有归类码()
    {
        var llm = new ScriptedChatClient().Reply("答完了");
        var (harness, traces) = Build(llm);

        await harness.RunAsync(new HarnessRunRequest("t1", "你好"));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Succeeded, trace.Status);
        Assert.Null(trace.ErrorCode);
    }

    // 负控：catch 分支改回 catch 具体类型（不再归类）→ code 变成 null，本条红。
    [Fact]
    public async Task 模型抛异常时记录里是failed且归类为unhandled_error()
    {
        var llm = new ScriptedChatClient().Fail(new InvalidOperationException("boom"));
        var (harness, traces) = Build(llm);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(new HarnessRunRequest("t2", "你好")));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Failed, trace.Status);
        Assert.Equal(AgentRunCode.Unhandled, trace.ErrorCode);
    }

    // ── 停因的结构化依据 ────────────────────────────────────────────

    // 负控：把 Observed 写死成常量（例如恒等于 Limit），或改成从 Message 正则抠数字 → 本条红。
    // 纯类测试：停因产生在 ②，本格只要求「这个依据能搬进记录」。
    [Fact]
    public void 循环终止的记录里能读出观测次数与阈值()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions
        {
            MaxIterations = 6,
            MaxRepeatedToolCalls = 3,
            MaxTokensPerRun = 0
        });

        AgentRunStop? stop = null;
        for (var i = 0; i < 3; i++)
            stop = guard.InspectToolCall(new ToolCallObservation(0, "echo", "{\"text\":\"hi\"}"));

        Assert.NotNull(stop);
        Assert.Equal(AgentRunCode.LoopDetected, stop!.Code);
        Assert.NotNull(stop.Detail);
        Assert.Equal("repeated_tool_calls", stop.Detail!.Kind);
        Assert.Equal(3L, stop.Detail.Observed);
        Assert.Equal(3L, stop.Detail.Limit);
        Assert.Equal("echo", stop.Detail.Subject);
    }

    // 负控：把 Observed 改成「配置上限」而不是「实际轮次」→ 本条红（数字相等会掩盖）。
    [Fact]
    public void 跑满迭代的记录里能读出轮次与预算()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions
        {
            MaxIterations = 2,
            MaxRepeatedToolCalls = 0
        });

        // 最后一轮（下标 1 = MaxIterations - 1）还在调工具，即被截断。
        guard.InspectToolCall(new ToolCallObservation(1, "echo", "{}"));
        var stop = guard.InspectCompletion();

        Assert.NotNull(stop);
        Assert.Equal(AgentRunCode.IterationLimit, stop!.Code);
        Assert.NotNull(stop.Detail);
        Assert.Equal("iteration_budget", stop.Detail!.Kind);
        Assert.Equal(2L, stop.Detail.Observed);
        Assert.Equal(2L, stop.Detail.Limit);
    }

    // 前两条的观测值必然等于阈值（第 N 次触发，N == Limit），写死成配置值也绿——区分不出来。
    // 这条故意让 used > 上限，才把「实际用量」与「配置上限」两个数字分开。
    // 负控：把 Observed 写成 Limit（10）或写成基线快照（0）→ 本条红。
    [Fact]
    public void token预算终止的记录里能读出实际用量与上限()
    {
        var usage = new FakeUsage(new AgentUsage(0, 0, 0));
        var guard = new AgentRunGuard(
            new AgentLoopOptions { MaxIterations = 6, MaxRepeatedToolCalls = 0, MaxTokensPerRun = 10 },
            usage);

        // 第一次 8，未超上限：不该停。
        usage.Current = new AgentUsage(1, 8, 0);
        Assert.Null(guard.InspectToolCall(new ToolCallObservation(0, "echo", "{}")));

        // 第二次 15，超出：停，且依据里是 15 与 10，不是 8、不是 0、不是 10 与 10。
        usage.Current = new AgentUsage(2, 15, 0);
        var stop = guard.InspectToolCall(new ToolCallObservation(0, "echo2", "{}"));

        Assert.NotNull(stop);
        Assert.Equal(AgentRunCode.BudgetExceeded, stop!.Code);
        Assert.NotNull(stop.Detail);
        Assert.Equal("token_budget", stop.Detail!.Kind);
        Assert.Equal(15L, stop.Detail.Observed);
        Assert.Equal(10L, stop.Detail.Limit);
    }

    // ── 用量 ────────────────────────────────────────────────────────

    // 负控：改成读 IModelRuntime.GetUsage() 的进程累计差值 → 断言落空（替身故意报 999000）。
    // 单轮无工具，所以「本次 run 的用量」= 这一次 chat 调用的用量，不存在聚合歧义。
    [Fact]
    public async Task 一次run结束后记录里的token等于本次调用上报的用量()
    {
        var llm = new ScriptedChatClient().Reply("答完了");
        var (harness, traces) = Build(llm, clientSeenByHarness: new UsageChatClient(llm, 10, 5));

        await harness.RunAsync(new HarnessRunRequest("t3", "你好"));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(10L, trace.InputTokens);
        Assert.Equal(5L, trace.OutputTokens);
    }

    // 供应商没报用量时必须留 null，不能写 0——0 会被读成「这次真的没花钱」。
    // 注意：本条单独看是恒真的（不实现读取路径时字段本来就是 null），必须与上一条一起读。
    // 负控：把 null 兜底成 0 → 本条红。
    [Fact]
    public async Task 供应商没报用量时记null不记0()
    {
        var llm = new ScriptedChatClient().Reply("答完了");
        var (harness, traces) = Build(llm);

        await harness.RunAsync(new HarnessRunRequest("t4", "你好"));

        var trace = Assert.Single(traces.Saved);
        Assert.Null(trace.InputTokens);
        Assert.Null(trace.OutputTokens);
    }

    // 流式没有最终 AgentResponse，用量只能从 update 里的 UsageContent 捞（2026-11-13 探针证实 MEA 会透传）。
    // 负控：把累加删掉、改成读进程累计 → 本条红；把"没上报"补成 0 → 上一条红。
    [Fact]
    public async Task 流式run的用量与同步run同口径()
    {
        var llm = new ScriptedChatClient().Reply("答完了");
        var (harness, traces) = Build(llm, clientSeenByHarness: new UsageStreamClient(llm, 10, 5));

        var events = new List<HarnessStreamEvent>();
        await foreach (var evt in harness.StreamAsync(new HarnessRunRequest("t9", "你好")))
            events.Add(evt);

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(10L, trace.InputTokens);
        Assert.Equal(5L, trace.OutputTokens);
        Assert.Contains(events, e => e.Kind == HarnessStreamEventKind.Completed);
    }

    // ── 步骤与截断 ──────────────────────────────────────────────────

    // 负控：把 Truncate 的置位去掉（截断照做、标志不传）→ 本条红。
    // 这条同时钉住「④ 采集 → ⑥ 记录」的搬运：只改一侧等于没改。
    [Fact]
    public async Task 工具结果被截断时该步带截断标志()
    {
        var llm = new ScriptedChatClient().ToolCall("Long").Reply("好了");
        var (harness, traces) = Build(
            llm,
            tools: [AIFunctionFactory.Create(new LongTool().Long, "Long")],
            maxResultChars: 100);

        await harness.RunAsync(new HarnessRunRequest("t5", "请调用 Long"));

        var trace = Assert.Single(traces.Saved);
        var toolStep = Assert.Single(trace.Steps, s => s.Kind == "tool");
        Assert.True(toolStep.Truncated, "工具结果被截断了，但记录里没有标志");
        Assert.Equal(100, toolStep.Result?.Length);
    }

    // ── 完整性 ──────────────────────────────────────────────────────

    // 负控：把中断态也报成 complete=true（恒真默认值）→ 本条红。
    // 判据：被中断时可能有一条工具调用正在执行、没进步骤序列，所以这条记录只覆盖了一部分。
    [Fact]
    public async Task 超时中断的记录能声明自己不完整()
    {
        var llm = new ScriptedChatClient().Delay(TimeSpan.FromSeconds(3), "太慢了");
        var (harness, traces) = Build(
            llm, loopOptions: new AgentLoopOptions { TimeoutSeconds = 1 });

        await Assert.ThrowsAsync<AgentRunTimeoutException>(
            () => harness.RunAsync(new HarnessRunRequest("t6", "你好")));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Timeout, trace.Status);
        Assert.False(trace.Complete, "被中断的记录必须声明自己不完整");
    }

    // ── 出口的硬要求 ────────────────────────────────────────────────

    // 负控：把 PersistQuietlyAsync 的 CancellationToken.None 换成调用方的 token → 本条红。
    // 为什么重要：调用方取消了，不代表这次 run 可以不记账。
    [Fact]
    public async Task 失败路径落库不跟随调用方的取消令牌()
    {
        var llm = new ScriptedChatClient().Fail(new InvalidOperationException("boom"));
        var (harness, traces) = Build(llm);

        using var cts = new CancellationTokenSource();   // CanBeCanceled = true，但未取消
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(new HarnessRunRequest("t7", "你好"), cts.Token));

        var token = Assert.Single(traces.Tokens);
        Assert.False(token.CanBeCanceled, "落库用了调用方的令牌，取消时会丢记录");
    }

    // 负控：把 PrepareRunAsync 挪回 try 外 → 本条红（异常冒泡，库料为空）。
    // IAgentHarness 的注释承诺「无论成败都留一条轨迹」，现在对任何非 Hub 调用方都不成立。
    [Fact]
    public async Task 准备阶段抛异常也留下一条轨迹()
    {
        var llm = new ScriptedChatClient().Reply("答完了");
        var (harness, traces) = Build(
            llm,
            sessions: new ThrowingStore(),
            memory: new MemoryOptions { SessionEnabled = true });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunAsync(new HarnessRunRequest("t8", "你好")));

        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.Failed, trace.Status);
    }

    // ── 两条结构约束（内核边界）──────────────────────────────────────

    // 负控：在 Harness 的决策路径里加一次轨迹读取 → 本条红。
    // 现状红：GetRunAsync（回放门面）直接读 _traces.GetAsync；拆读写口后才绿。
    [Fact]
    public void 内核只依赖写口_不在决策路径上读轨迹()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Core", "Runtime", "AgentWorkbench.Runtime.Harness", "AgentHarnessService.cs"));

        Assert.DoesNotContain("_traces.GetAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_traces.QueryAsync", source, StringComparison.Ordinal);
    }

    // 负控：给 Observability 加一条 Persistence / Domain 引用 → 本条红（且编译期也会先报 CS0234）。
    [Fact]
    public void 观测内核只引Shared_不拖存储与领域进编译期()
    {
        var csproj = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Core", "Engineering", "AgentWorkbench.Observability",
            "AgentWorkbench.Observability.csproj"));

        var referenced = csproj
            .Split('\n')
            .Where(line => line.Contains("ProjectReference", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        var only = Assert.Single(referenced);
        Assert.Contains("AgentWorkbench.Shared", only, StringComparison.Ordinal);
    }

    /// <summary>从测试输出目录往上找带 <c>AgentWorkbench.slnx</c> 的目录。</summary>
    private static string RepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgentWorkbench.slnx")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        throw new DirectoryNotFoundException("未找到 AgentWorkbench.slnx，无法定位仓库根。");
    }
}
