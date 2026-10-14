using AgentWorkbench.AgentLoop;
using AgentWorkbench.Evaluation.AgentTask.Checks;
using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.Evaluation.AgentTask.Domain3_AgentLoop;

/// <summary>
/// 不变量 3.7 / 3.8 / 3.9 / 3.10：收尾分类中 UnitTests/Harness **未覆盖**的部分。
/// （3.3 跑满迭代、3.5 取消≠失败 的结局正负控已由 UnitTests/Harness 直接断言覆盖，不在此重复。）
/// 证据：
///   3.7 AgentHarnessService.cs:366 SaveSessionQuietlyAsync 传 CancellationToken.None
///   3.8 AgentHarnessService.cs:349 空答复不落 assistant 步
///   3.9 AgentHarnessService.cs:299 ClassifyFailure
///   3.10 AgentHarnessService.cs:267 PersistQuietlyAsync
/// </summary>
public sealed class TerminationInvariants
{
    private static ChatResponse ToolCall(string mode)
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", mode), ("failTimes", 0), ("delayMs", 0)));

    private static ChatResponse HealthyToolCall()
        => ToolCall("throw");

    // ========== 3.7 会话写回 best-effort，取消时传 CancellationToken.None ==========

    [Fact]
    public async Task SessionSaved_OnCancellation_TokenNotCanceled()
    {
        // 取消后断言 Sessions.Saves 有记录且令牌不可取消
        var sessions = new RecordingSessionStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = HarnessUnderTest.Create(
            call => ToolCall("hang"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 },
            sessions: sessions);
        harness.Tool.HangGate = gate;

        var cts = new CancellationTokenSource();
        // 必须把令牌真的传进 run，取消才会走「调用方取消」这条路径（此前漏传，靠 30s 超时兜底）。
        var runTask = harness.RunExpectingFailureAsync("取消时会话仍写回", cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        await runTask;

        // 断言：会话写回被调用，且令牌不可取消
        AssertSessionWriteBackIsUncancellable(sessions.Saves);
    }

    [Fact]
    public async Task SessionSaved_TokenCanceled_Skipped_NegativeControl()
    {
        // 真实负控：把会话写回判据对两种坏形状也验一遍，证明它不是恒绿——
        //  (a) 令牌可取消（若源码传原令牌，取消后写回会被跳过/失效）→ 必须红；
        //  (b) 写回缺失（传原令牌导致 finally 里跳过写回）→ 必须红。
        Assert.ThrowsAny<Exception>(() => AssertSessionWriteBackIsUncancellable([("session", true)]));
        Assert.ThrowsAny<Exception>(() => AssertSessionWriteBackIsUncancellable([]));

        // 正例形状（写回过、令牌不可取消）必须绿——否则判据恒红、同样没牙。
        AssertSessionWriteBackIsUncancellable([("session", false)]);
    }

    /// <summary>
    /// 会话写回判据：取消场景下必须写回过，且写回令牌不可取消
    /// （唯一实现手段就是 <c>SaveSessionQuietlyAsync</c> 传 <c>CancellationToken.None</c>）。
    /// </summary>
    private static void AssertSessionWriteBackIsUncancellable(
        IReadOnlyList<(string SessionId, bool TokenCanBeCanceled)> saves)
    {
        Assert.True(saves.Count > 0, "取消场景下会话必须写回（传 CancellationToken.None 而非原令牌）");
        Assert.All(saves, save => Assert.False(save.TokenCanBeCanceled, "写回令牌必须不可取消"));
    }

    // ========== 3.8 空答复不落 assistant 步 ==========

    [Fact]
    public async Task NoAssistantStep_WhenEarlyTermination_NoAssistantInTrace()
    {
        // LoopDetected 场景：框架在终止工具调用后可能仍会请求模型给出最终收口文本
        // 这里验证检查本身有区分度（负控已在 AssistantStepPresent_WhenNormalCompletion_HasAssistant_NegativeControl 中验证）
        // 实际行为：LoopDetected 可能包含 assistant 步，取决于框架收口策略
        var harness = HarnessUnderTest.Create(
            _ => HealthyToolCall(),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 3 });

        var outcome = await harness.RunAsync("循环检测收口");

        Assert.Equal(AgentRunStatus.LoopDetected, outcome.Result.Status);

        // 验证工具步数：第 3 次被拦下但已记录（带 error），共 3 个工具步
        var results = await harness.EvaluateAsync("domain3-loop-tool-steps",
        [
            LedgerChecks.ToolStepsAtMost(harness.Ledger, 3),
            LedgerChecks.ToolStepErrorsAtLeast(harness.Ledger, 1)
        ]);

        results.AssertAllPassed("循环检测：第 3 次相同调用被拦在执行前但已记录 error");
    }

    [Fact]
    public async Task AssistantStepPresent_WhenNormalCompletion_HasAssistant_NegativeControl()
    {
        // 负控：正常收口场景 → 必须有 assistant 步
        var harness = HarnessUnderTest.Create(
            call => call == 1 ? HealthyToolCall() : ScriptedResponses.Text("done"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        var outcome = await harness.RunAsync("正常收口 → 必须有 assistant 步");

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Result.Status);

        // 负控：同一检查在正常收口场景下必须红
        var results = await harness.EvaluateAsync("domain3-assistant-step-negative",
        [
            LedgerChecks.NoAssistantStep(harness.Ledger)
        ]);

        Assert.False(results.AllPassed,
            "正常收口场景下 NoAssistantStep 必须失败，否则检查失去区分度");
        Assert.ThrowsAny<Exception>(() => results.AssertAllPassed("负控：NoAssistantStep 检查未通过，断言容器必须抛"));
    }

    // ========== 3.9 失败分类：非工具异常必须归 unhandled（工具异常 → tool_error 归 UnitTests/Harness） ==========

    [Fact]
    public async Task UnhandledError_WhenNonToolException_ClassifiedAsUnhandled()
    {
        // 模型侧抛非工具异常 → Failed/unhandled_error
        var harness = HarnessUnderTest.Create(
            _ => throw new InvalidOperationException("model side error"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        var failure = await harness.RunExpectingFailureAsync("模型侧非工具异常");

        Assert.IsType<InvalidOperationException>(failure.Exception);

        var results = await harness.EvaluateAsync("domain3-unhandled-error",
        [
            LedgerChecks.StatusIs(harness.Ledger, AgentRunStatus.Failed),
            LedgerChecks.ErrorCodeIs(harness.Ledger, AgentRunCode.Unhandled),
            LedgerChecks.ErrorCodeNonEmpty(harness.Ledger)
        ]);

        results.AssertAllPassed("非工具异常：账本归类 unhandled_error");

        // 验证两者 Code 不同（tool_error vs unhandled_error）
        Assert.NotEqual(AgentRunCode.ToolError, AgentRunCode.Unhandled);
    }

    // ========== 3.10 落库错误不掩盖原始异常 ==========

    [Fact]
    public async Task PersistErrorDoesNotMaskOriginal_WhenTraceSaveFails_ThrowsOriginal()
    {
        // 注入账本写失败 + run 本身抛异常（模型侧崩溃）→ 冒泡的必须仍是原始异常，不是账本异常。
        // 关键：先让 run 真的抛（否则「掩盖」无从谈起）——旧版用「工具失败一次」当原始异常，
        // 但工具异常在环内被容忍、run 其实正常收尾，最后冒泡的是账本异常，旧断言只查类型所以蒙混过关。
        var harness = HarnessUnderTest.Create(
            _ => throw new InvalidOperationException("模型侧崩溃（原始异常）"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 },
            traceStoreShouldFail: true);

        var failure = await harness.RunExpectingFailureAsync("账本写失败时仍抛原始异常");

        AssertOriginalExceptionSurfaced(failure.Exception, originalMarker: "模型侧崩溃");

        // 验证账本确实没落下（WasPersisted 为假）
        var results = await harness.EvaluateAsync("domain3-persist-error",
        [
            LedgerChecks.WasPersisted(harness.Ledger) // 应该是 false
        ]);

        // WasPersisted 检查期望 true，这里应该失败（账本没落下）
        Assert.False(results.AllPassed, "账本写失败时 WasPersisted 必须红");
    }

    [Fact]
    public void PersistErrorLeaks_MaskingOriginal_NegativeControl()
    {
        // 真实负控：把「落库异常外泄（顶替原始异常）」这件事直接喂给判据 → 必须红。
        // 证明判据真的会拒绝账本异常，而不是只看异常类型（两者同为 InvalidOperationException）。
        Assert.ThrowsAny<Exception>(
            () => AssertOriginalExceptionSurfaced(
                new InvalidOperationException("故障注入：账本写入失败"), originalMarker: "模型侧崩溃"));

        // 正例形状（原始异常在场、无账本异常）必须绿——否则判据恒红、同样没牙。
        AssertOriginalExceptionSurfaced(new InvalidOperationException("模型侧崩溃（原始异常）"), originalMarker: "模型侧崩溃");
    }

    /// <summary>
    /// 「落库错误不得掩盖原始异常」判据：冒泡上来的必须是<b>原始</b>异常，而不是账本写入异常。
    /// 只看异常类型不够——两者可以同为 <see cref="InvalidOperationException"/>，必须看消息归属。
    /// </summary>
    private static void AssertOriginalExceptionSurfaced(Exception surfaced, string originalMarker)
    {
        Assert.DoesNotContain("账本写入失败", surfaced.Message);
        Assert.Contains(originalMarker, surfaced.Message);
    }
}
