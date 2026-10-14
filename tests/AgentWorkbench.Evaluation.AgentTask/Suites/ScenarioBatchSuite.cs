using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.Evaluation.AgentTask.Scenarios;
using AgentWorkbench.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.Evaluation.AgentTask.Suites;

/// <summary>
/// 跨场景批量套件 —— 本工程存在的理由。
///
/// MAF 在这里提供两样不可替代的东西：<b>批量组织</b>（N 个样本进一个容器）与
/// <b>汇总结论</b>（Passed / Failed / Total + 断言出口）。判据 100% 自建（<c>LedgerChecks</c>）。
///
/// 与 <c>Batch/</c> 的区别：<c>Batch/</c> 证明「一个夹具的 N 条样本能进同一容器」；
/// 本套件证明「<b>N 个不同配置的场景</b>能进同一容器」——这才是「评估范围扩展到整个任务」的落点。
/// 每个场景一台独立 harness（不共享实例），最后把全部样本与判据并进一次 <c>EvaluateAsync</c>。
/// </summary>
public class ScenarioBatchSuite
{
    /// <summary>全部场景契约必须一次成立：Total = 场景数，Failed = 0。</summary>
    [Fact]
    public async Task AllScenarios_ScoreInOneContainer()
    {
        var scenarios = TaskScenarios.All;
        var harnesses = new List<(TaskScenario Scenario, HarnessUnderTest Harness)>();
        foreach (var scenario in scenarios)
            harnesses.Add((scenario, await ScenarioExecutor.ExecuteAsync(scenario)));

        var ledger = MergeLedger(harnesses);
        var checks = harnesses
            .SelectMany(entry => ScenarioExecutor.ChecksFor(entry.Scenario, ledger, entry.Harness))
            .ToArray();

        // 任取一台 harness 当判分宿主；其余场景的样本作为额外样本并入同一次判分。
        var host = harnesses[0].Harness;
        var rest = harnesses.Skip(1).SelectMany(entry => entry.Harness.Cases);

        var results = await host.EvaluateAsync("task-scenario-sweep", rest, checks);

        Assert.Equal(scenarios.Count, results.Total);
        Assert.Equal(scenarios.Count, results.Passed);
        Assert.Equal(0, results.Failed);
        results.AssertAllPassed("全部场景的终态契约必须成立");
    }

    /// <summary>
    /// 负控：把某个场景的契约故意写反，该样本必须被计成 Failed，且断言容器必须抛。
    /// 若这条也全绿，说明汇总恒报通过 —— 上一条的「全绿」就没有信息量。
    /// </summary>
    [Fact]
    public async Task BrokenContract_FailsItsItem_NegativeControl()
    {
        var scenario = TaskScenarios.HappyPath with { ExpectStatus = AgentRunStatus.Failed };
        var harness = await ScenarioExecutor.ExecuteAsync(scenario);

        var ledger = MergeLedger([(scenario, harness)]);
        var checks = ScenarioExecutor.ChecksFor(scenario, ledger, harness);

        var results = await harness.EvaluateAsync("task-scenario-negative", checks);

        Assert.Equal(1, results.Total);
        Assert.Equal(1, results.Failed);
        Assert.False(results.AllPassed);
        Assert.ThrowsAny<Exception>(() => results.AssertAllPassed("契约不成立，容器必须抛"));
    }

    /// <summary>
    /// 判据限定生效：一条样本只被自己那份契约判定，不被别的场景的契约误伤。
    /// 两个契约互斥的场景（正常收口 vs 循环终止）并进同一容器，必须两条都过。
    /// </summary>
    [Fact]
    public async Task ScopedChecks_DoNotCrossContaminate()
    {
        var happy = TaskScenarios.HappyPath;
        var loop = TaskScenarios.LoopDetected;

        var happyHarness = await ScenarioExecutor.ExecuteAsync(happy);
        var loopHarness = await ScenarioExecutor.ExecuteAsync(loop);

        var ledger = MergeLedger([(happy, happyHarness), (loop, loopHarness)]);
        var checks = new[]
        {
            ScenarioExecutor.ChecksFor(happy, ledger, happyHarness),
            ScenarioExecutor.ChecksFor(loop, ledger, loopHarness)
        }.SelectMany(array => array).ToArray();

        var results = await happyHarness.EvaluateAsync("task-scenario-scoped", loopHarness.Cases, checks);

        Assert.Equal(2, results.Total);
        Assert.Equal(2, results.Passed);
        results.AssertAllPassed("互斥契约互不误伤：正常收口与循环终止都必须过自己的判据");
    }

    /// <summary>
    /// 驱动轨：让 MAF 自己「执行」——把 query 交给它，由 <c>HarnessAgent</c> 真的跑一遍 harness，再判分。
    /// 与回放轨（上面三条）互补：回放轨我们先把 run 跑完再喂结果，失败 / 取消路径靠它才进得了容器；
    /// 驱动轨则证明 MAF 的「组织 + 执行」这一半也是通的。
    /// </summary>
    [Fact]
    public async Task DriveTrack_MafRunsTheAgent_ThenScores()
    {
        var scenario = TaskScenarios.HappyPath;
        var harness = ScenarioExecutor.Build(scenario);

        // 判据全部用 MAF 现成原语（不造轮子）：工具被调用 + 答复非空。
        // 前提已修 —— HarnessConversation 不再自己补 user 消息，故 EvalItem.Response = 真答复，
        // NonEmpty 恢复区分度（此前它含 query，恒真）。
        var results = await harness.EvaluateDrivenAsync(
            "task-scenario-drive",
            [scenario.Name],
            [EvalChecks.ToolCalledCheck([FaultInjectorTool.ToolName]), EvalChecks.NonEmpty(1)]);

        Assert.Equal(1, results.Total);
        results.AssertAllPassed("驱动轨：MAF 跑完 harness 后，工具调用与最终答复都必须可判");
    }

    /// <summary>
    /// 根因回归闸：我方转换出的 <c>AgentResponse</c> **不得**自带 user 消息（user 由 MAF 按 query 回填）。
    /// 自带就会让 <c>EvalItem.Response</c> 变成「query + 答复」的拼接文本 → <c>EvalChecks</c> 的
    /// 文本原语（`NonEmpty`/`ContainsExpected`/`KeywordCheck`）恒真。谁再加回来，这里立刻红。
    /// </summary>
    [Fact]
    public async Task AgentResponse_LeavesUserMessageToMaf()
    {
        var harness = await ScenarioExecutor.ExecuteAsync(TaskScenarios.HappyPath);

        Assert.NotEmpty(harness.Cases);
        Assert.All(harness.Cases, item =>
            Assert.DoesNotContain(item.Response.Messages, message => message.Role == ChatRole.User));
    }

    /// <summary>
    /// MAF 原语自身的负控：空答复必须判红。若这条也绿，说明 <c>EvalChecks.NonEmpty</c> 无区分度——
    /// 那驱动轨里「答复非空」那半句就没有信息量。
    /// </summary>
    [Fact]
    public void NonEmptyCheck_HasTeeth_EmptyResponseFails()
    {
        var result = EvalChecks.NonEmpty(1)(new EvalItem("任意 query", string.Empty));
        Assert.False(result.Passed, "空答复必须判红，否则该原语是假门禁");
    }

    private static IReadOnlyDictionary<string, LedgerFact> MergeLedger(
        IEnumerable<(TaskScenario Scenario, HarnessUnderTest Harness)> harnesses)
        => harnesses
            .SelectMany(entry => entry.Harness.Ledger)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
}
