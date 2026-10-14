using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.Evaluation.AgentTask.Domain3_AgentLoop;

/// <summary>
/// 域 3（执行控制 Agent Loop / Harness）：MAF 容器的**新增价值**验证。
///
/// 职责边界（见 docs/测试标准/测试分层职责.md）：结局的正负控（预算 / 迭代 / 取消 / 失败分类）
/// 已由 <c>UnitTests/Harness</c> 用直接断言覆盖，不在此重复。
/// 本文件只回答一件事：**轨迹重建成对话后，MAF 的 EvalCheck 判得对不对**——
/// 即「检查本身有区分度」（同一批检查在对立场景必须红）。
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
}
