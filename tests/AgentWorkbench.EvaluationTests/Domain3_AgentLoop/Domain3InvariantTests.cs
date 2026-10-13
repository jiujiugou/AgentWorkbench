using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.EvaluationTests.Fixtures;
using AgentWorkbench.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.EvaluationTests.Domain3_AgentLoop;

/// <summary>
/// 域 3（执行控制 Agent Loop / Harness）首批：MAF Evaluation 容器落地 + 判据自建。
///
/// 与 UnitTests 的分工（避免重复建设）：<c>AgentHarnessExecutionControlTests</c> 已用直接断言
/// 覆盖了预算 / 迭代 / 取消 / 失败分类的正负控；本文件只放 MAF 容器的**新增价值**部分——
/// 批量驱动（跑一批 outcome）→ 轨迹重建 → EvalCheck 判分 → AssertAllPassed 门禁，
/// 以及「检查本身有区分度」的负控（同一检查在对立场景必须红）。
/// </summary>
public sealed class Domain3InvariantTests
{
    private static AgentLoopOptions Defaults()
        => new() { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 3 };

    private static ChatResponse HealthyToolCall()
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", "throw"), ("failTimes", 0), ("delayMs", 0)));

    [Fact]
    public async Task ToolLoop_PassesMafContainer_AndClassifiesSucceeded()
    {
        // 场景 harness-tool：第 1 轮调工具，第 2 轮收口——正例。
        var harness = HarnessUnderTest.Create(
            call => call == 1 ? HealthyToolCall() : ScriptedResponses.Text("done"), Defaults());

        var outcome = await harness.RunAsync("按脚本调一次工具再收口");

        // 判据自建（直接断言）：结局分类必须与「模型答完了」可区分。
        Assert.Equal(AgentRunStatus.Succeeded, outcome.Result.Status);
        Assert.Null(outcome.Result.Code);
        Assert.Equal("done", outcome.Result.Text);
        Assert.Equal(1, outcome.ToolCalls);
        Assert.Equal(2, outcome.ModelCalls);
        Assert.Equal(AgentRunStatus.Succeeded, outcome.Trace.Status);

        // MAF 容器：轨迹重建后，工具调用与答复非空都可判。
        var results = await harness.EvaluateAsync("domain3-harness-tool",
        [
            EvalChecks.ToolCalledCheck([FaultInjectorTool.ToolName]),
            EvalChecks.NonEmpty(1)
        ]);

        results.AssertAllPassed("工具环场景：工具必须被调用，且最终答复非空");
    }

    [Fact]
    public async Task DegradedNoTool_FailsToolCalledCheck_NegativeControl()
    {
        // 负控：同一批检查，场景换成「模型直接给文本、不调工具」——ToolCalledCheck 必须红。
        // 若这里也全绿，说明该检查没有区分度（《标准》§3.2：去掉检测器后仍全绿 = 没测到东西）。
        var harness = HarnessUnderTest.Create(_ => ScriptedResponses.Text("degraded"), Defaults());

        var outcome = await harness.RunAsync("同一问题，模型选择直接答");

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Result.Status);
        Assert.Equal(0, outcome.ToolCalls);

        var results = await harness.EvaluateAsync("domain3-reject-tools",
        [
            EvalChecks.ToolCalledCheck([FaultInjectorTool.ToolName]),
            EvalChecks.NonEmpty(1)
        ]);

        Assert.False(results.AllPassed,
            "不调工具的场景下 ToolCalledCheck 必须失败，否则该检查失去区分度（负控失效）");

        // 断言容器本身也必须有牙：检查未通过时 AssertAllPassed 必须抛——
        // 否则「全绿」可能只是门禁是空的（假绿）。
        Assert.ThrowsAny<Exception>(() => results.AssertAllPassed("负控：检查未通过，断言容器必须抛"));
    }

    [Fact]
    public async Task RepeatedIdenticalCalls_StopAsLoopDetected()
    {
        // 不变量 3.1：同工具 + 同参数连续 MaxRepeatedToolCalls 次 → LoopDetected 且干净收口。
        var harness = HarnessUnderTest.Create(
            _ => HealthyToolCall(),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 3 });

        var outcome = await harness.RunAsync("同一调用反复来");

        Assert.Equal(AgentRunStatus.LoopDetected, outcome.Result.Status);
        Assert.Equal(AgentRunCode.LoopDetected, outcome.Result.Code);
        Assert.Equal(2, outcome.ToolCalls);   // 第 3 次相同调用在真正执行前就被拦下
        Assert.Equal(AgentRunStatus.LoopDetected, outcome.Trace.Status);

        // 循环终止同样留下可判分的对话：工具调用发生过，且不混入失败。
        var results = await harness.EvaluateAsync("domain3-harness-loop",
        [
            EvalChecks.ToolCalledCheck([FaultInjectorTool.ToolName])
        ]);

        results.AssertAllPassed("循环被干净收口：轨迹里必须看得到工具调用");
    }

    [Fact]
    public async Task SameScript_LoopDetectionDisabled_RunsToIterationLimit()
    {
        // 3.1 负控：同一份脚本，只把重复检测关掉（0）——结局必须从 loop_detected 变成 iteration_limit。
        var harness = HarnessUnderTest.Create(
            _ => HealthyToolCall(),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        var outcome = await harness.RunAsync("同一调用反复来，但关掉循环检测");

        Assert.Equal(AgentRunStatus.IterationLimit, outcome.Result.Status);
        Assert.Equal(AgentRunCode.IterationLimit, outcome.Result.Code);
        Assert.Equal(10, outcome.ToolCalls);
    }
}
