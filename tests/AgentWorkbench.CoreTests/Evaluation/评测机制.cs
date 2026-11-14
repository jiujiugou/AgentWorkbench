using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkbench.Evaluation;
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

namespace AgentWorkbench.CoreTests.Evaluation;

/// <summary>
/// 评测模块自己的行为判据（对应 docs/评测对象与判据.md 第五、六节）。
///
/// 定位：验「投影对不对、判据读的是不是落库事实、改坏会不会红」。
/// 不验六部件本身——那 99 条 CoreTests 已经钉住了；也不验六句，那属动作域题库。
/// 每条都写明负控，且判据自身有 R0 自证（读不回轨迹时必须红，不能恒绿）。
/// </summary>
public sealed class 评测机制
{
    // ── 替身 ───────────────────────────────────────────────────────

    /// <summary>内存轨迹库：SaveAsync 返回 1-based runId，GetAsync 真能读回来。</summary>
    private sealed class TraceStore : IExecutionTraceStore
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
            => Task.FromResult<IReadOnlyList<ExecutionTrace>>(Saved.AsReadOnly());
    }

    private sealed class FixedFactory(IChatClient client) : IChatClientFactory
    {
        public Task<ChatEndpoint?> ResolveAsync(string? modelOverride, CancellationToken cancellationToken)
            => Task.FromResult<ChatEndpoint?>(new ChatEndpoint(null, "http://localhost", "test-key", "test-model"));

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

    private sealed class Echo
    {
        public int Calls { get; private set; }
        public string Run(string text)
        {
            Calls++;
            return "echo:" + text;
        }
    }

    // ── 构造测试数据 ───────────────────────────────────────────────

    private static HarnessRunResult Result(long runId = 7, params HarnessStep[] steps)
        => new(runId, "s-eval", "答完了", 1, "test-model", 12,
            steps.Length == 0 ? [new HarnessStep(0, "assistant", null, null, "答完了")] : steps,
            AgentRunStatus.Succeeded);

    private static ExecutionTrace Trace(
        string status = "succeeded",
        string? code = null,
        RunStopDetail? stop = null,
        long? input = null,
        long? output = null,
        bool complete = true,
        params ExecutionStep[] steps)
        => new("s-eval", status, 1, "test-model", 12, null, code,
            steps.Length == 0 ? [new ExecutionStep(0, "assistant", null, null, "答完了")] : steps,
            input, output, complete, stop);

    private static IReadOnlyList<EvalCheck> AllChecks(ExecutionTrace? trace) =>
    [
        KernelChecks.Outcome(trace, "succeeded", null),
        KernelChecks.Completeness(trace, true),
        KernelChecks.UsagePerRun(trace, null, null),
        KernelChecks.ToolStepCount(trace, 0),
        KernelChecks.Iterations(trace, 1),
    ];

    // ── 投影：MAF 内置判定必须能从对话里读到工具调用 ────────────────

    [Fact]
    public async Task 投影后_MAF内置工具判定能从对话里读到调用()
    {
        var result = Result(7,
            new HarnessStep(0, "tool", "Echo", "{\"text\":\"hi\"}", "echo:hi"),
            new HarnessStep(1, "assistant", null, null, "答完了"));

        var checks = new List<EvalCheck> { EvalChecks.ToolCalledCheck("Echo") };
        var results = await KernelEvaluation.EvaluateAsync(result, "调一下 Echo", (ExecutionTrace?)null,
            _ => checks);

        Assert.True(results.AllPassed,
            $"Passed={results.Passed} Failed={results.Failed} Error={results.Error}");
        Assert.Equal(1, results.Total);
    }

    [Fact]
    public void 投影保留每次调用的身份_同名工具多次调用不撞callId()
    {
        var result = Result(7,
            new HarnessStep(0, "tool", "Echo", "{\"text\":\"a\"}", "echo:a"),
            new HarnessStep(0, "tool", "Echo", "{\"text\":\"b\"}", "echo:b"));

        var conversation = HarnessResponseProjection.ToConversation(result);
        var ids = conversation
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Select(c => c.CallId)
            .ToList();

        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct().Count()); // 两次调用被压成同一个 callId，回放会串
    }

    // ── 判据读的是落库事实 ─────────────────────────────────────────

    [Fact]
    public async Task 全部判据符合时_聚合结果为全通过()
    {
        var trace = Trace();
        var results = await KernelEvaluation.EvaluateAsync(Result(), "你好", trace, AllChecks);

        Assert.True(results.AllPassed);
        Assert.Equal(1, results.Total);
        Assert.Equal(0, results.Failed);
    }

    [Fact]
    public async Task 按RunId读不回轨迹时_每条判据都必须红()
    {
        // R0 自证：若「读不到」被实现成跳过或恒真，这里会全绿，评测就是假绿。
        var store = new TraceStore();
        var results = await KernelEvaluation.EvaluateAsync(Result(runId: 99), "你好", store, AllChecks);

        Assert.False(results.AllPassed);
        Assert.Equal(0, results.Passed);
        Assert.True(results.Failed > 0);
    }

    [Fact]
    public async Task 落库闭环_写进去的事实能被按RunId读回来用于评测()
    {
        var store = new TraceStore();
        await store.SaveAsync(Trace());
        var results = await KernelEvaluation.EvaluateAsync(Result(runId: 1), "你好", store, AllChecks);

        Assert.True(results.AllPassed);
    }

    [Fact]
    public async Task 终态判据_状态改坏就红()
    {
        var results = await KernelEvaluation.EvaluateAsync(
            Result(), "你好", Trace(status: "failed", code: AgentRunCode.Unhandled),
            t => [KernelChecks.Outcome(t, "succeeded", null)]);

        Assert.False(results.AllPassed);
        Assert.Equal(1, results.Failed);
    }

    [Fact]
    public async Task 停因判据_观测值与阈值分别比对_写死配置值也红()
    {
        // 文档里的恒真风险：若 Observed 写死成 Limit，「used > limit」这类场景照样绿。
        // 这里故意让 Observed=3、Limit=2 不相等，再把 Observed 改成 2 验负控。
        var good = new RunStopDetail("token_budget", 3, 2, null);
        var pass = await KernelEvaluation.EvaluateAsync(Result(), "冲爆预算", Trace(stop: good),
            t => [KernelChecks.StopDetail(t, "token_budget", 3, 2)]);
        Assert.True(pass.AllPassed);

        var mutated = good with { Observed = good.Limit };
        var fail = await KernelEvaluation.EvaluateAsync(Result(), "冲爆预算", Trace(stop: mutated),
            t => [KernelChecks.StopDetail(t, "token_budget", 3, 2)]);
        Assert.False(fail.AllPassed, "观测值被写成阈值时必须红，否则这条判据恒真");
    }

    [Fact]
    public async Task 用量判据_进程累计冒充本次值会红()
    {
        // 本次调用没上报（null），进程累计器报 999000；判据期望 null。
        var polluted = Trace(input: 999_000, output: 999_000);
        var results = await KernelEvaluation.EvaluateAsync(Result(), "你好", polluted,
            t => [KernelChecks.UsagePerRun(t, null, null)]);

        Assert.False(results.AllPassed);
    }

    [Fact]
    public async Task 完整性判据_中断态报成完整会红()
    {
        var results = await KernelEvaluation.EvaluateAsync(Result(), "超时", Trace(complete: true),
            t => [KernelChecks.Completeness(t, false)]);

        Assert.False(results.AllPassed);
    }

    [Fact]
    public async Task 截断标志判据_没有该步不静默通过()
    {
        // 步骤只有 assistant，却要求 iteration=0 的工具步带截断标志：必须红，不能跳过。
        var results = await KernelEvaluation.EvaluateAsync(Result(), "超长结果", Trace(),
            t => [KernelChecks.TruncationFlag(t, 0, true)]);

        Assert.False(results.AllPassed);

        var good = Trace(steps: new ExecutionStep(0, "tool", "Echo", null, "截断", null, true));
        var pass = await KernelEvaluation.EvaluateAsync(Result(), "超长结果", good,
            t => [KernelChecks.TruncationFlag(t, 0, true)]);
        Assert.True(pass.AllPassed);
    }

    [Fact]
    public void 判据结果的名字与理由落在正确的字段上()
    {
        // 自证 EvalCheckResult(bool, Reason, CheckName) 的参数位置没写反：
        // 写反了不影响 Passed，却会让失败报告里的原因和名字互换，排障时全是误导。
        // 两个分支都要覆盖——只测「读不回轨迹」的分支，改坏 Result() 时不会红。
        var missing = KernelChecks.Outcome(null, "succeeded")(new EvalItem("q", "a"));
        Assert.False(missing.Passed);
        Assert.Equal("终态与归类码", missing.CheckName);
        Assert.Contains("没有读回轨迹", missing.Reason);

        var mismatched = KernelChecks.Outcome(Trace(status: "failed"), "succeeded")(new EvalItem("q", "a"));
        Assert.False(mismatched.Passed);
        Assert.Equal("终态与归类码", mismatched.CheckName);
        Assert.Contains("期望", mismatched.Reason);
    }

    // ── 真 Harness 闭环：跑一次、落库、读回、评测 ───────────────────

    private static (IAgentHarness Harness, TraceStore Traces) Build(
        ScriptedChatClient llm, Echo tool, AgentLoopOptions? loop = null)
    {
        var traces = new TraceStore();
        var memory = new MemoryOptions { SessionEnabled = false };
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();

        var contextServices = new ServiceCollection();
        contextServices.AddLogging();
        contextServices.AddNitroContext(new ConfigurationBuilder().Build());
        var context = contextServices.BuildServiceProvider().GetRequiredService<IRequestContextComposer>();

        var pipeline = new ToolExecutionPipeline(
            Options.Create(new ToolExecutionOptions { MaxResultChars = 400 }),
            new AllowAll(), NullLoggerFactory.Instance, services);

        var harness = new AgentHarnessService(
            new FixedFactory(llm),
            new PassthroughRuntime(),
            new FixedCatalog(AIFunctionFactory.Create(tool.Run, "Echo")),
            pipeline,
            context,
            new SessionStore(Options.Create(memory), TimeProvider.System),
            new SessionRunGate(),
            Options.Create(memory),
            new AsyncLocalMemoryScopeAccessor(),
            new AgentLoopService(
                Options.Create(loop ?? new AgentLoopOptions
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

    [Fact]
    public async Task 真Harness跑一轮_落库读回_评测闭环全通过()
    {
        var llm = new ScriptedChatClient()
            .ToolCall("Echo", new Dictionary<string, object?> { ["text"] = "hi" })
            .Reply("答完了");
        var tool = new Echo();
        var (harness, traces) = Build(llm, tool);

        var run = await harness.RunAsync(new HarnessRunRequest("eval-1", "调一下 Echo"));
        Assert.Equal(1, tool.Calls); // 先证明这一轮真的执行过工具，下面的绿才有意义

        var results = await KernelEvaluation.EvaluateAsync(run, "调一下 Echo", traces, t =>
        [
            EvalChecks.ToolCalledCheck("Echo"),
            KernelChecks.Outcome(t, AgentRunStatus.Succeeded, null),
            KernelChecks.Completeness(t, true),
            KernelChecks.ToolStepCount(t, 1),
        ]);

        Assert.True(results.AllPassed,
            $"Passed={results.Passed} Failed={results.Failed} Error={results.Error}");
        results.AssertAllPassed();
    }

    [Fact]
    public async Task 真Harness诱导重复调用_停因从落库轨迹被评到()
    {
        // 负例：模型一直以相同参数要同一个工具，守卫应当提前终止，且停因带观测值与阈值。
        var llm = new ScriptedChatClient()
            .ToolCall("Echo", new Dictionary<string, object?> { ["text"] = "same" });
        llm.RepeatLast = true;
        var tool = new Echo();
        var (harness, traces) = Build(llm, tool, new AgentLoopOptions
        {
            MaxIterations = 6, TimeoutSeconds = 5, MaxRepeatedToolCalls = 2
        });

        var run = await harness.RunAsync(new HarnessRunRequest("eval-2", "一直调"));
        var trace = Assert.Single(traces.Saved);
        Assert.Equal(AgentRunStatus.LoopDetected, trace.Status);

        var results = await KernelEvaluation.EvaluateAsync(run, "一直调", traces, t =>
        [
            KernelChecks.Outcome(t, AgentRunStatus.LoopDetected, AgentRunCode.LoopDetected),
            KernelChecks.StopDetail(t, "repeated_tool_calls", 2, 2, "Echo"),
        ]);

        Assert.True(results.AllPassed,
            $"Passed={results.Passed} Failed={results.Failed} Error={results.Error}");

        // 负控：把期望的观测值改成 3（与阈值不等），判据必须红。
        var mutated = await KernelEvaluation.EvaluateAsync(run, "一直调", traces, t =>
        [
            KernelChecks.StopDetail(t, "repeated_tool_calls", 3, 2, "Echo"),
        ]);
        Assert.False(mutated.AllPassed);
    }
}
