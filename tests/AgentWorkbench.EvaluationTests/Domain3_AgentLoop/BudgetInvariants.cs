using AgentWorkbench.AgentLoop;
using AgentWorkbench.EvaluationTests.Checks;
using AgentWorkbench.EvaluationTests.Fixtures;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.EvaluationTests.Domain3_AgentLoop;

/// <summary>
/// 不变量 3.2：token 预算
/// 证据：AgentRunGuard.cs:82 DetectBudgetOverage、ToolExecutionPipeline.cs:103 InspectToolCall 调用点
/// </summary>
public sealed class BudgetInvariants
{
    private const long TokensPerRequest = 100;

    private static ChatResponse ToolCall(string mode)
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", mode), ("failTimes", 0), ("delayMs", 0)));

    [Fact]
    public async Task BudgetExceeded_WhenTokensExceed_StopsBeforeExecution()
    {
        // 预算 50、每请求 100 token → 第 1 轮工具调用被拦在执行之前
        // 需要 UsageMeter 让 MeteredModelRuntime 真正记录用量
        var meter = new UsageMeter();
        var harness = HarnessUnderTest.Create(
            call => ToolCall("throw"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxTokensPerRun = 50, MaxRepeatedToolCalls = 0 },
            new MeteredModelRuntime(meter) { TokensPerRequest = TokensPerRequest });

        var query = "预算 50，单次 100 → 必须在第 1 轮工具调用前被拦下";
        var outcome = await harness.RunAsync(query);

        Assert.Equal(AgentRunStatus.BudgetExceeded, outcome.Result.Status);
        Assert.Equal(AgentRunCode.BudgetExceeded, outcome.Result.Code);
        Assert.Equal(0, outcome.ToolCalls); // 第 1 次工具调用在真正执行前就被拦下

        // MAF 容器：账本层面的判据
        var results = await harness.EvaluateAsync("domain3-budget-exceeded",
        [
            LedgerChecks.StatusIs(harness.Ledger, AgentRunStatus.BudgetExceeded),
            LedgerChecks.ErrorCodeIs(harness.Ledger, AgentRunCode.BudgetExceeded),
            LedgerChecks.ToolStepErrorsAtLeast(harness.Ledger, 1), // 预算破了，工具步带 error（被拦在执行前）
            LedgerChecks.ErrorCodeNonEmpty(harness.Ledger)
        ]);

        results.AssertAllPassed("预算耗尽：必须在执行前拦下，且账本留下 budget_exceeded");
    }

    [Fact]
    public async Task BudgetDisabled_RunsToIterationLimit_NegativeControl()
    {
        // 负控：同一脚本，只把预算关掉（=0）→ 结局必须变 iteration_limit
        // 若这里也是 BudgetExceeded，说明预算检查根本没生效（假绿）
        var meter = new UsageMeter();
        var harness = HarnessUnderTest.Create(
            call => ToolCall("throw"),
            new AgentLoopOptions { MaxIterations = 3, TimeoutSeconds = 30, MaxTokensPerRun = 0, MaxRepeatedToolCalls = 0 },
            new MeteredModelRuntime(meter) { TokensPerRequest = TokensPerRequest });

        var outcome = await harness.RunAsync("同一脚本，预算=0 → 必须跑满迭代");

        Assert.Equal(AgentRunStatus.IterationLimit, outcome.Result.Status);
        Assert.Equal(AgentRunCode.IterationLimit, outcome.Result.Code);
        Assert.Equal(3, outcome.ToolCalls);

        // 负控：同一批账本检查，在预算=0 的场景下必须红（证明检查有区分度）
        var results = await harness.EvaluateAsync("domain3-budget-disabled",
        [
            LedgerChecks.StatusIs(harness.Ledger, AgentRunStatus.BudgetExceeded),
            LedgerChecks.ErrorCodeIs(harness.Ledger, AgentRunCode.BudgetExceeded)
        ]);

        Assert.False(results.AllPassed,
            "预算关掉时 budget_exceeded 检查必须失败，否则该检查失去区分度");
        Assert.ThrowsAny<Exception>(() => results.AssertAllPassed("负控：预算检查未通过，断言容器必须抛"));
    }
}