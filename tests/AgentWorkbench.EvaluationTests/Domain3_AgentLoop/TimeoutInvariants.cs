using AgentWorkbench.AgentLoop;
using AgentWorkbench.EvaluationTests.Checks;
using AgentWorkbench.EvaluationTests.Fixtures;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.EvaluationTests.Domain3_AgentLoop;

/// <summary>
/// 不变量 3.4 / 3.4b：超时分类
/// 证据：AgentLoopService.cs:34 ExecuteAsync 抛 AgentRunTimeoutException
///       AgentHarnessService.cs:200 catch AgentRunTimeoutException → run_timeout
///       AgentHarnessService.cs:209 catch TimeoutException → model_timeout
///       IAgentLoop.cs:32 AgentRunTimeoutException 继承 TimeoutException
/// </summary>
public sealed class TimeoutInvariants
{
    private static ChatResponse ToolCall(string mode)
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", mode), ("failTimes", 0), ("delayMs", 0)));

    [Fact]
    public async Task RunTimeout_WhenHangTool_ThrowsAgentRunTimeoutException()
    {
        // TimeoutSeconds=1 + hang 工具 → 抛 AgentRunTimeoutException
        // run 级墙钟预算由 AgentLoop 施加（约 1.2s 真实等待）
        var harness = HarnessUnderTest.Create(
            call => ToolCall("hang"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 1, MaxRepeatedToolCalls = 0 });

        // 失败路径必须用 RunExpectingFailureAsync 捕获异常并读取账本
        var failure = await harness.RunExpectingFailureAsync("run 级超时 1s，工具 hang");

        Assert.IsType<AgentRunTimeoutException>(failure.Exception);
        Assert.Equal(AgentRunStatus.Timeout, failure.Exception is AgentRunTimeoutException ? AgentRunStatus.Timeout : AgentRunStatus.Failed); // 仅为编译时确认

        // MAF 容器：账本层面的判据（从 failure 的查询键查账本）
        var results = await harness.EvaluateAsync("domain3-run-timeout",
        [
            LedgerChecks.StatusIs(harness.Ledger, AgentRunStatus.Timeout),
            LedgerChecks.ErrorCodeIs(harness.Ledger, AgentRunCode.RunTimeout),
            LedgerChecks.ErrorCodeNonEmpty(harness.Ledger)
        ]);

        results.AssertAllPassed("run 级超时：必须抛 AgentRunTimeoutException，账本留下 run_timeout");
    }

    [Fact]
    public async Task RunTimeoutDisabled_Succeeds_NegativeControl()
    {
        // 负控：同一脚本结构，但工具快速返回且模型第 2 轮收口 → Succeeded
        // 若这里也是 Timeout，说明超时检查根本没生效（假绿）
        var harness = HarnessUnderTest.Create(
            call => call == 1 ? ToolCall("throw") : ScriptedResponses.Text("done"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        var outcome = await harness.RunAsync("同一脚本，工具快速返回且模型收口 → 必须成功");

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Result.Status);
        Assert.Null(outcome.Result.Code);

        // 负控：同一批账本检查，在成功场景下必须红（证明检查有区分度）
        var results = await harness.EvaluateAsync("domain3-run-timeout-disabled",
        [
            LedgerChecks.StatusIs(harness.Ledger, AgentRunStatus.Timeout),
            LedgerChecks.ErrorCodeIs(harness.Ledger, AgentRunCode.RunTimeout)
        ]);

        Assert.False(results.AllPassed,
            "工具快速返回且模型收口时 run_timeout 检查必须失败，否则该检查失去区分度");
        Assert.ThrowsAny<Exception>(() => results.AssertAllPassed("负控：超时检查未通过，断言容器必须抛"));
    }

    [Fact]
    public async Task ModelTimeout_WhenModelThrowsTimeoutException_ClassifiedSeparately()
    {
        // 单次模型请求超时：ModelRuntime 抛 TimeoutException → 账本归类 model_timeout
        // 必须与 run_timeout 区分开（二者是继承关系，最易混）
        var harness = HarnessUnderTest.Create(
            _ => ScriptedResponses.Text("done"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 },
            new ThrowingModelRuntime(new TimeoutException("model request timeout")));

        // 失败路径必须用 RunExpectingFailureAsync
        var failure = await harness.RunExpectingFailureAsync("模型请求超时");

        Assert.IsType<TimeoutException>(failure.Exception);
        Assert.IsNotType<AgentRunTimeoutException>(failure.Exception);

        // MAF 容器：账本层面的判据
        var results = await harness.EvaluateAsync("domain3-model-timeout",
        [
            LedgerChecks.StatusIs(harness.Ledger, AgentRunStatus.Timeout),
            LedgerChecks.ErrorCodeIs(harness.Ledger, AgentRunCode.ModelTimeout),
            LedgerChecks.ErrorCodeNonEmpty(harness.Ledger)
        ]);

        results.AssertAllPassed("单次模型请求超时：账本必须留下 model_timeout（非 run_timeout）");
    }

    [Fact]
    public async Task ModelTimeout_NotWrappedAsRunTimeout_DirectAssertion()
    {
        // 直接断言：模型请求超时抛出的异常类型**不是** AgentRunTimeoutException
        // 二者是继承关系（AgentRunTimeoutException : TimeoutException），最易在 catch 顺序上混淆
        var harness = HarnessUnderTest.Create(
            _ => ScriptedResponses.Text("done"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 },
            new ThrowingModelRuntime(new TimeoutException("model request timeout")));

        var failure = await harness.RunExpectingFailureAsync("模型请求超时");

        Assert.IsType<TimeoutException>(failure.Exception);
        Assert.NotNull(failure.Exception);
        // 关键断言：不是 AgentRunTimeoutException（否则 catch 顺序错了，run_timeout 会吞掉 model_timeout）
        Assert.IsNotType<AgentRunTimeoutException>(failure.Exception!);
    }
}