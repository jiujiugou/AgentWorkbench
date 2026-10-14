using AgentWorkbench.AgentLoop;
using AgentWorkbench.Evaluation.AgentTask.Checks;
using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.Shared;
using Xunit;

namespace AgentWorkbench.Evaluation.AgentTask.Domain3_AgentLoop;

/// <summary>
/// 不变量 3.4b：单次模型请求超时必须与 run 级墙钟超时**分开归类**。
/// 证据：
///   AgentLoopService.cs:34 ExecuteAsync 抛 AgentRunTimeoutException（run 级）
///   AgentHarnessService.cs:200 catch AgentRunTimeoutException → run_timeout
///   AgentHarnessService.cs:209 catch TimeoutException → model_timeout
///   IAgentLoop.cs:32 AgentRunTimeoutException 继承 TimeoutException（最易在 catch 顺序上混淆）
///
/// run 级超时本身已由 UnitTests/Harness（RunWallClockTimeout_IsTimeoutWithRunTimeoutCode）覆盖，
/// 此处只锁「两者可分」——那是 UnitTests 没测的判据。
/// </summary>
public sealed class TimeoutInvariants
{
    [Fact]
    public async Task ModelTimeout_WhenModelThrowsTimeoutException_ClassifiedSeparately()
    {
        // 单次模型请求超时：ModelRuntime 抛 TimeoutException → 账本归类 model_timeout
        // 必须与 run_timeout 区分开（二者是继承关系，最易混）
        var harness = HarnessUnderTest.Create(
            _ => ScriptedResponses.Text("done"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 },
            new ThrowingModelRuntime(new TimeoutException("model request timeout")));

        // 失败路径必须用 RunExpectingFailureAsync 捕获异常并读取账本
        var failure = await harness.RunExpectingFailureAsync("模型请求超时");

        Assert.IsType<TimeoutException>(failure.Exception);
        // 关键断言：不是 AgentRunTimeoutException——否则 catch 顺序错了，run_timeout 会吞掉 model_timeout
        Assert.IsNotType<AgentRunTimeoutException>(failure.Exception);

        var results = await harness.EvaluateAsync("domain3-model-timeout",
        [
            LedgerChecks.StatusIs(harness.Ledger, AgentRunStatus.Timeout),
            LedgerChecks.ErrorCodeIs(harness.Ledger, AgentRunCode.ModelTimeout),
            LedgerChecks.ErrorCodeNonEmpty(harness.Ledger)
        ]);

        results.AssertAllPassed("单次模型请求超时：账本必须留下 model_timeout（非 run_timeout）");
    }
}
