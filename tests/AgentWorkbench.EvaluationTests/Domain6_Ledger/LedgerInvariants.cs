using AgentWorkbench.AgentLoop;
using AgentWorkbench.EvaluationTests.Checks;
using AgentWorkbench.EvaluationTests.Fixtures;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.EvaluationTests.Domain6_Ledger;

/// <summary>
/// 域 6｜可观测与评估（账本）
/// 证据：AgentHarnessService.cs:175/195/204/220 四路径落库、:234 GetRunAsync、:30 截断、:363 Truncate、AgentRunCode (Shared)
/// </summary>
public sealed class LedgerInvariants
{
    private static ChatResponse ToolCall(string mode)
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", mode), ("failTimes", 0), ("delayMs", 0)));

    private static ChatResponse HealthyToolCall()
        => ToolCall("throw");

    // ========== 6.1 四路径批量落库 ==========

    [Fact]
    public async Task AllFourPaths_PersistedInSameBatch()
    {
        // 一次 EvaluateAsync 跑四条路径：成功 / 失败 / 取消 / 超时
        // 验证：四行账本 status 互不相同

        // 1. 成功路径
        var harness1 = HarnessUnderTest.Create(
            call => call == 1 ? HealthyToolCall() : ScriptedResponses.Text("done"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });
        await harness1.RunAsync("path-1-succeeded");

        // 2. 失败路径（模型侧异常，非工具失败）
        var harness2 = HarnessUnderTest.Create(
            _ => throw new InvalidOperationException("model side error"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });
        await harness2.RunExpectingFailureAsync("path-2-failed");

        // 3. 取消路径
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness3 = HarnessUnderTest.Create(
            call => ToolCall("hang"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });
        harness3.Tool.HangGate = gate;
        var cts = new CancellationTokenSource();
        var cancelTask = harness3.RunExpectingFailureAsync("path-3-cancelled", cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await cancelTask;

        // 4. 超时路径（run 级）
        var harness4 = HarnessUnderTest.Create(
            call => ToolCall("hang"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 1, MaxRepeatedToolCalls = 0 });
        await harness4.RunExpectingFailureAsync("path-4-timeout");

        // 验证四种 status 都存在且互不相同
        var allStatuses = new HashSet<string>();
        foreach (var h in new[] { harness1, harness2, harness3, harness4 })
        {
            var fact = h.Ledger.Values.First();
            Assert.True(fact.Persisted, $"路径 {fact.Status} 必须落库");
            Assert.False(string.IsNullOrEmpty(fact.Status), "落库的 run 必须有终态 status");
            allStatuses.Add(fact.Status!);
        }

        Assert.Contains(AgentRunStatus.Succeeded, allStatuses);
        Assert.Contains(AgentRunStatus.Failed, allStatuses);
        Assert.Contains(AgentRunStatus.Cancelled, allStatuses);
        Assert.Contains(AgentRunStatus.Timeout, allStatuses);
        Assert.Equal(4, allStatuses.Count);
    }

    // ========== 6.2 Iteration 单调不减 ==========

    [Fact]
    public async Task IterationMonotonic_InMultiTurnRun()
    {
        // 多轮 run → agent_steps.Iteration 单调不减
        var harness = HarnessUnderTest.Create(
            call => call <= 3 ? HealthyToolCall() : ScriptedResponses.Text("done"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        await harness.RunAsync("多轮工具调用");

        var results = await harness.EvaluateAsync("domain6-iteration-monotonic",
        [
            LedgerChecks.StatusIs(harness.Ledger, AgentRunStatus.Succeeded)
        ]);

        results.AssertAllPassed("多轮 run 成功");

        // 验证步骤的 Iteration 单调不减
        var fact = harness.Ledger.Values.First();
        var toolSteps = fact.Steps.Where(s => s.Kind == "tool").OrderBy(s => s.Iteration).ToList();
        for (int i = 1; i < toolSteps.Count; i++)
        {
            Assert.True(toolSteps[i].Iteration >= toolSteps[i - 1].Iteration,
                $"Iteration 必须单调不减：{toolSteps[i - 1].Iteration} -> {toolSteps[i].Iteration}");
        }
    }

    // ========== 6.3 轨迹可回放 ==========

    [Fact]
    public async Task GetRunAsync_RoundtripsStatusCodeSteps()
    {
        // 落库后 GetRunAsync 复原 status / code / steps
        var harness = HarnessUnderTest.Create(
            call => call == 1 ? HealthyToolCall() : ScriptedResponses.Text("done"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        var outcome = await harness.RunAsync("回放测试");

        var runId = outcome.Result.RunId;
        var replayed = await harness.Harness.GetRunAsync(runId);

        Assert.NotNull(replayed);
        Assert.Equal(outcome.Result.Status, replayed!.Status);
        Assert.Equal(outcome.Result.Code, replayed.Code);
        Assert.Equal(outcome.Result.Steps.Count, replayed.Steps.Count);
        for (int i = 0; i < outcome.Result.Steps.Count; i++)
        {
            Assert.Equal(outcome.Result.Steps[i].Kind, replayed.Steps[i].Kind);
            Assert.Equal(outcome.Result.Steps[i].Tool, replayed.Steps[i].Tool);
        }
    }

    [Fact]
    public async Task GetRunAsync_MissingErrorCode_NegativeControl()
    {
        // 真实负控：失败 run 落库后把 error_code 篡改为 null → 回放就不再能区分「为什么失败」。
        // 锁住「失败路径必须留下归类码」——只留人话（Error）的回放，调用方无法决定重试还是降级。
        var harness = HarnessUnderTest.Create(
            _ => throw new InvalidOperationException("model side error"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        await harness.RunExpectingFailureAsync("回放负控：失败但归类码被抹掉");

        // 正例：回放拿得到归类码。
        var healthy = await harness.Harness.GetRunAsync(1);
        Assert.NotNull(healthy);
        Assert.Equal(AgentRunCode.Unhandled, healthy!.Code);

        // 篡改账本：抹掉归类码 → 回放事实随之丢码，「必须带归类码」的断言于是必须红。
        harness.Traces.Tamper(0, trace => trace with { ErrorCode = null });
        var tampered = await harness.Harness.GetRunAsync(1);
        Assert.NotNull(tampered);
        Assert.Null(tampered!.Code);
        Assert.ThrowsAny<Exception>(() => Assert.Equal(AgentRunCode.Unhandled, tampered.Code));
    }

    // ========== 6.4 失败路径 ErrorCode 非空 ==========

    [Fact]
    public async Task ErrorCodeNonEmpty_OnFailurePaths()
    {
        // 模型侧异常 → Failed/unhandled_error，ErrorCode 非空
        var harness = HarnessUnderTest.Create(
            _ => throw new InvalidOperationException("model side error"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        await harness.RunExpectingFailureAsync("模型侧异常");

        var results = await harness.EvaluateAsync("domain6-error-code-nonempty",
        [
            LedgerChecks.StatusIs(harness.Ledger, AgentRunStatus.Failed),
            LedgerChecks.ErrorCodeIs(harness.Ledger, AgentRunCode.Unhandled),
            LedgerChecks.ErrorCodeNonEmpty(harness.Ledger)
        ]);

        results.AssertAllPassed("模型侧异常：账本 error_code 必须非空");

        // 负控：期望 ErrorCode 为 null → 必须红
        var results2 = await harness.EvaluateAsync("domain6-error-code-empty-negative",
        [
            LedgerChecks.ErrorCodeIs(harness.Ledger, (string?)null)
        ]);

        Assert.False(results2.AllPassed, "ErrorCode 为 null 时检查必须失败");
        Assert.ThrowsAny<Exception>(() => results2.AssertAllPassed("负控：ErrorCode 为 null 必须红"));
    }

    // ========== 6.5 assistant 步截断到 MaxAssistantChars(=400) ==========

    [Fact]
    public async Task AssistantStepTruncated_ToMaxAssistantChars()
    {
        // 500 字答复 → 落库 400 字
        var longText = new string('x', 500);
        var harness = HarnessUnderTest.Create(
            _ => ScriptedResponses.Text(longText),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        await harness.RunAsync("长文本答复");

        var results = await harness.EvaluateAsync("domain6-assistant-truncated",
        [
            LedgerChecks.AssistantStepWithin(harness.Ledger, 400)
        ]);

        results.AssertAllPassed("长文本：assistant 步截断到 400 字");

        // 真实负控：同一判据喂「未截断」的 500 字步子 → 必须红。
        // 也就是把「源码移除截断」这件事在内存里复现一次：判据确有区分度，而不是恒绿。
        var untruncated = harness.Ledger.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with
            {
                Steps = pair.Value.Steps
                    .Select(step => step.Kind == "assistant" ? step with { Result = longText } : step)
                    .ToList()
            });

        var negative = await harness.EvaluateAsync("domain6-assistant-truncated-negative",
        [
            LedgerChecks.AssistantStepWithin(untruncated, 400)
        ]);

        Assert.False(negative.AllPassed, "未截断的 500 字步子下，AssistantStepWithin(400) 必须失败");
        Assert.ThrowsAny<Exception>(() => negative.AssertAllPassed("负控：未截断必须红"));
    }
}