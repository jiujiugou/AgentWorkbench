using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.EvaluationTests.Fixtures;
using AgentWorkbench.Observability;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.EvaluationTests.HarnessReplay;

/// <summary>
/// 阶段 3｜轨迹事件源与回放回归。
///
/// 病根：<see cref="IExecutionTraceStore"/> 此前「只写不读」——轨迹落了库，但没有任何测试消费它，
/// 于是「测试全绿」与「真实行为」之间没有连线，验收可以假绿。
/// 本文件把轨迹变成<b>回归源</b>：跑一次真 run → 落轨迹 → 从轨迹回放 → 对着独立预期判分，
/// 并且<b>篡改轨迹必须变红</b>（每条断言都有牙）。
///
/// 判据全部自建、确定性：模型是脚本化的（MockLlm），回放是「读历史 + 断言」，绝不重跑模型。
/// </summary>
public sealed class HarnessReplayTests
{
    private static AgentLoopOptions Defaults()
        => new() { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 3 };

    /// <summary>关掉重复检测：用于「同一工具连调多次」的脚本（否则会被判成循环收口）。</summary>
    private static AgentLoopOptions NoLoopDetection()
        => new() { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 };

    private static ChatResponse ToolCall()
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", "throw"), ("failTimes", 0), ("delayMs", 0)));

    // ========== 正例：回放事实 == 独立预期 == 原 run 事实 ==========

    [Fact]
    public async Task SucceededRun_ReplaysToolSequenceIterationsStatus()
    {
        // 脚本：第 1 轮调工具，第 2 轮收口。
        var harness = HarnessUnderTest.Create(
            call => call == 1 ? ToolCall() : ScriptedResponses.Text("done"), Defaults());

        var outcome = await harness.RunAsync("调一次工具再收口");
        var runId = outcome.Result.RunId;

        // 独立预期：来自脚本 + 选项，不看轨迹。
        var expected = new ReplayFacts([FaultInjectorTool.ToolName], Iterations: 2, AgentRunStatus.Succeeded, null);

        // ① 原 run 事实符合独立预期；② 回放事实 == 原 run 事实。
        TraceReplay.AssertMatches(expected, ReplayFacts.From(outcome.Result));
        TraceReplay.AssertMatches(ReplayFacts.From(outcome.Result), await TraceReplay.ReadAsync(harness.Harness, runId));
    }

    [Fact]
    public async Task MultiToolRun_ReplaysSequenceInOrder()
    {
        // 脚本：连调 3 次工具，第 4 轮收口——序列不仅要「数量对」，还要「顺序对」。
        var harness = HarnessUnderTest.Create(
            call => call <= 3 ? ToolCall() : ScriptedResponses.Text("done"), NoLoopDetection());

        var outcome = await harness.RunAsync("连调三次工具");

        var expected = new ReplayFacts(
            [FaultInjectorTool.ToolName, FaultInjectorTool.ToolName, FaultInjectorTool.ToolName],
            Iterations: 4, AgentRunStatus.Succeeded, null);

        TraceReplay.AssertMatches(expected, ReplayFacts.From(outcome.Result));
        TraceReplay.AssertMatches(
            ReplayFacts.From(outcome.Result),
            await TraceReplay.ReadAsync(harness.Harness, outcome.Result.RunId));
    }

    [Fact]
    public async Task LoopDetectedRun_ReplaysTerminalStatus()
    {
        // 同一调用反复来 → 循环收口。终态必须是 loop_detected 且带归类码（不是「成功的空答复」）。
        var harness = HarnessUnderTest.Create(_ => ToolCall(), Defaults());

        var outcome = await harness.RunAsync("同一调用反复来");

        var replayed = await TraceReplay.ReadAsync(harness.Harness, outcome.Result.RunId);
        Assert.Equal(AgentRunStatus.LoopDetected, replayed.Status);
        Assert.Equal(AgentRunCode.LoopDetected, replayed.ErrorCode);
        Assert.NotEmpty(replayed.ToolSequence);
        Assert.All(replayed.ToolSequence, tool => Assert.Equal(FaultInjectorTool.ToolName, tool));

        TraceReplay.AssertMatches(ReplayFacts.From(outcome.Result), replayed);
    }

    [Fact]
    public async Task FailedRun_ReplaysErrorClassification()
    {
        // 模型侧崩溃：run 以抛出收场，没有结果对象——只能靠轨迹回放「留下了什么」。
        var harness = HarnessUnderTest.Create(
            _ => throw new InvalidOperationException("故障注入：模型侧崩溃"), Defaults());

        await harness.RunExpectingFailureAsync("必失败的 run");

        // 失败路径没有 outcome.Result，回放是唯一入口：runId=1，终态 failed / unhandled_error。
        var replayed = await TraceReplay.ReadAsync(harness.Harness, 1);
        Assert.Equal(AgentRunStatus.Failed, replayed.Status);
        Assert.Equal(AgentRunCode.Unhandled, replayed.ErrorCode);
        Assert.Empty(replayed.ToolSequence);
    }

    // ========== 回放是「读历史」，不是「重新跑」 ==========

    [Fact]
    public async Task Replay_DoesNotRerun_ModelOrTool()
    {
        // 设计红线：回放必须确定性、零副作用。若回放偷偷重跑，模型/工具计数会涨——这里锁死。
        var harness = HarnessUnderTest.Create(
            call => call == 1 ? ToolCall() : ScriptedResponses.Text("done"), Defaults());

        var outcome = await harness.RunAsync("回放不得重跑");
        var toolCallsAfterRun = harness.Tool.Calls;
        var modelCallsAfterRun = harness.Client.Calls;

        var first = await TraceReplay.ReadAsync(harness.Harness, outcome.Result.RunId);
        var second = await TraceReplay.ReadAsync(harness.Harness, outcome.Result.RunId);

        Assert.Equal(toolCallsAfterRun, harness.Tool.Calls);
        Assert.Equal(modelCallsAfterRun, harness.Client.Calls);
        TraceReplay.AssertMatches(first, second);   // 同一轨迹回放两次，事实必须逐字相同
    }

    // ========== 负控：篡改轨迹 → 回放事实漂移 → 必须红 ==========

    /// <summary>跑一次健康 run 并把 runId 交给 <paramref name="tamper"/> 篡改后再回放，返回回放事实。</summary>
    private static async Task<(HarnessUnderTest Harness, ReplayFacts Original, ReplayFacts Tampered)> RunThenTamperAsync(
        Func<ExecutionTrace, ExecutionTrace> tamper)
    {
        var harness = HarnessUnderTest.Create(
            call => call == 1 ? ToolCall() : ScriptedResponses.Text("done"), Defaults());

        var outcome = await harness.RunAsync("先跑真的，再篡改轨迹");
        var original = ReplayFacts.From(outcome.Result);

        harness.Traces.Tamper(0, tamper);   // runId=1 → 索引 0
        var tampered = await TraceReplay.ReadAsync(harness.Harness, outcome.Result.RunId);
        return (harness, original, tampered);
    }

    [Fact]
    public async Task NegativeControl_TamperedToolSequence_GoesRed()
    {
        // 把轨迹里的工具名换成另一把 → 回放序列与原 run 不符 → 判据必须抛。
        var (_, original, tampered) = await RunThenTamperAsync(trace => trace with
        {
            Steps = trace.Steps
                .Select(step => step.Kind == "tool" ? step with { Tool = "not_a_real_tool" } : step)
                .ToList()
        });

        Assert.ThrowsAny<Exception>(() => TraceReplay.AssertMatches(original, tampered));
    }

    [Fact]
    public async Task NegativeControl_TamperedIterations_GoesRed()
    {
        // 篡改迭代次数（把「跑了 2 轮」写成「跑了 3 轮」）→ 必须红。
        var (_, original, tampered) = await RunThenTamperAsync(trace => trace with { Iterations = trace.Iterations + 1 });

        Assert.ThrowsAny<Exception>(() => TraceReplay.AssertMatches(original, tampered));
    }

    [Fact]
    public async Task NegativeControl_TamperedStatus_GoesRed()
    {
        // 把 succeeded 篡改成 failed（伪造终态）→ 必须红。这是最危险的一类篡改。
        var (_, original, tampered) = await RunThenTamperAsync(trace => trace with { Status = AgentRunStatus.Failed });

        Assert.ThrowsAny<Exception>(() => TraceReplay.AssertMatches(original, tampered));
    }

    [Fact]
    public async Task NegativeControl_DroppedSteps_GoesRed()
    {
        // 丢掉全部步骤（模拟「轨迹写了一半」）→ 回放重建不出工具序列 → 必须红。
        var (_, original, tampered) = await RunThenTamperAsync(trace => trace with { Steps = [] });

        Assert.Empty(tampered.ToolSequence);
        Assert.ThrowsAny<Exception>(() => TraceReplay.AssertMatches(original, tampered));
    }

    [Fact]
    public async Task NegativeControl_MissingTrace_GoesRed()
    {
        // 「回放不到」绝不能被当成通过：不存在的 runId 必须抛，而不是返回空事实后静默放行。
        var harness = HarnessUnderTest.Create(call => ScriptedResponses.Text("done"), Defaults());
        await harness.RunAsync("只跑一次");

        await Assert.ThrowsAnyAsync<Exception>(() => TraceReplay.ReadAsync(harness.Harness, runId: 999));
    }

    [Fact]
    public void ReplayJudge_HasTeeth_MismatchedFactsThrow()
    {
        // 判据自身的负控：四个字段各自错一个，AssertMatches 都必须抛——
        // 少这一段，「正例全绿」可能只是它根本不比较（假门禁）。
        var baseline = new ReplayFacts([FaultInjectorTool.ToolName], Iterations: 2, AgentRunStatus.Succeeded, null);

        Assert.ThrowsAny<Exception>(() => TraceReplay.AssertMatches(baseline, baseline with { Status = AgentRunStatus.Failed }));
        Assert.ThrowsAny<Exception>(() => TraceReplay.AssertMatches(baseline, baseline with { ErrorCode = AgentRunCode.ToolError }));
        Assert.ThrowsAny<Exception>(() => TraceReplay.AssertMatches(baseline, baseline with { Iterations = 3 }));
        Assert.ThrowsAny<Exception>(() => TraceReplay.AssertMatches(baseline, baseline with { ToolSequence = [] }));

        // 同形事实必须绿（否则判据恒红，同样没牙）。
        TraceReplay.AssertMatches(baseline, baseline with { });
    }

    // ========== 查询面：按 sessionId / status 检索一批轨迹 ==========

    [Fact]
    public async Task QueryBySessionId_ReturnsOnlyThatSession_NewestFirst()
    {
        // 同一会话连跑两次 + 另一个会话跑一次 → 按 sessionId 过滤只应拿到本会话的两条，且最近优先。
        var harness = HarnessUnderTest.Create(call => ScriptedResponses.Text($"答复{call}"), Defaults());

        await harness.RunAsync("本会话第一轮", "session-replay");
        await harness.RunAsync("本会话第二轮", "session-replay");
        await harness.RunAsync("另一个会话", "session-other");

        var traces = await harness.Traces.QueryAsync(
            new ExecutionTraceQuery(SessionId: "session-replay"));

        Assert.Equal(2, traces.Count);
        Assert.All(traces, trace => Assert.Equal("session-replay", trace.SessionId));
        // 最近优先：第二条 run 的答复在前。
        Assert.Contains("答复2", traces[0].Steps.Last(s => s.Kind == "assistant").Result!);
    }

    [Fact]
    public async Task QueryByStatus_FiltersOutOtherTerminals()
    {
        // status 过滤必须真的过滤：成功的会话里查 failed，必须为空。
        var harness = HarnessUnderTest.Create(call => ScriptedResponses.Text("done"), Defaults());
        await harness.RunAsync("成功的一轮", "session-status");

        var failed = await harness.Traces.QueryAsync(
            new ExecutionTraceQuery(Status: AgentRunStatus.Failed));
        Assert.Empty(failed);

        var succeeded = await harness.Traces.QueryAsync(
            new ExecutionTraceQuery(Status: AgentRunStatus.Succeeded));
        Assert.Single(succeeded);
    }
}
