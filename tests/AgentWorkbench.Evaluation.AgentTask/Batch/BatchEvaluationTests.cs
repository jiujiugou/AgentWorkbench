using AgentWorkbench.AgentLoop;
using AgentWorkbench.Evaluation.AgentTask.Checks;
using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.Evaluation.AgentTask.Batch;

/// <summary>
/// MAF 容器的<b>唯一不可替代能力</b>：批量驱动 + 汇总（Passed / Failed / Total）。
///
/// 为什么单独成组：此前全部用例每条只跑 1 次 run，<c>EvaluateAsync</c> 拿到的样本数是 1 ——
/// 等于「用批量框架跑单条」，容器相对 xUnit 的增量归零（只剩包装成本与更差的失败定位）。
/// 本组专门证明它能吃 N 条，并把三种形态喂进同一个容器：
/// 单条成功、多场景、成功与失败混合。
///
/// 判据仍全部自建（<c>LedgerChecks</c>）；MAF 只负责「跑 N 条、数 Passed/Failed」。
/// </summary>
public class BatchEvaluationTests
{
    private static AgentLoopOptions Defaults() => new()
    {
        MaxIterations = 10,
        TimeoutSeconds = 30,
        MaxRepeatedToolCalls = 0
    };

    /// <summary>三个场景跑进同一个容器：Total 必须等于样本数，而不是 1。</summary>
    [Fact]
    public async Task ThreeRuns_AreScoredAsThreeItems()
    {
        var harness = HarnessUnderTest.Create(_ => ScriptedResponses.Text("done"), Defaults());

        await harness.RunAsync("样本一");
        await harness.RunAsync("样本二");
        await harness.RunAsync("样本三");

        Assert.Equal(3, harness.Cases.Count);

        var results = await harness.EvaluateAsync("batch-three", [LedgerChecks.WasPersisted(harness.Ledger)]);

        Assert.Equal(3, results.Total);
        Assert.Equal(3, results.Passed);
        Assert.Equal(0, results.Failed);
        results.AssertAllPassed("三条样本都必须落账");
    }

    /// <summary>
    /// 失败路径与成功路径进同一个容器：AgentTask 独有的武器之一
    /// （<c>RunExpectingFailureAsync</c> 把取消 / 超时 / 异常路径从账本重建成对话喂进判分）。
    /// </summary>
    [Fact]
    public async Task SuccessAndFailure_EnterSameBatch()
    {
        var harness = HarnessUnderTest.Create(
            call => call == 1
                ? ScriptedResponses.Text("done")
                : throw new InvalidOperationException("模型侧崩溃"),
            Defaults());

        await harness.RunAsync("成功样本");
        await harness.RunExpectingFailureAsync("失败样本");

        Assert.Equal(2, harness.Cases.Count);

        var results = await harness.EvaluateAsync("batch-mixed", [LedgerChecks.WasPersisted(harness.Ledger)]);

        Assert.Equal(2, results.Total);
        results.AssertAllPassed("成功与失败两条路径都必须留下账本行");
    }

    /// <summary>
    /// 负控：判据故意不成立时，汇总必须逐条计 Failed、且断言容器必须有牙。
    /// 若这条也全绿，说明汇总恒报通过 —— 那么上两条的「3/3 通过」就没有信息量。
    /// </summary>
    [Fact]
    public async Task FailingCheck_IsCountedPerItem_NegativeControl()
    {
        var harness = HarnessUnderTest.Create(_ => ScriptedResponses.Text("done"), Defaults());

        await harness.RunAsync("甲");
        await harness.RunAsync("乙");

        var results = await harness.EvaluateAsync(
            "batch-negative", [LedgerChecks.StatusIs(harness.Ledger, "no-such-status")]);

        Assert.Equal(2, results.Total);
        Assert.Equal(0, results.Passed);
        Assert.Equal(2, results.Failed);
        Assert.False(results.AllPassed);
        Assert.ThrowsAny<Exception>(() => results.AssertAllPassed("判据不成立，容器必须抛"));
    }

    /// <summary>
    /// 汇总随样本数变化：同一判据、同一夹具形状，1 条与 3 条的 Total 必须不同。
    /// 这是「容器真的按样本数计分」而非「永远报常数」的直接证伪。
    /// </summary>
    [Fact]
    public async Task Total_TracksSampleCount()
    {
        var one = HarnessUnderTest.Create(_ => ScriptedResponses.Text("done"), Defaults());
        await one.RunAsync("单条");
        var oneResult = await one.EvaluateAsync("batch-one", [LedgerChecks.WasPersisted(one.Ledger)]);

        var three = HarnessUnderTest.Create(_ => ScriptedResponses.Text("done"), Defaults());
        await three.RunAsync("一");
        await three.RunAsync("二");
        await three.RunAsync("三");
        var threeResult = await three.EvaluateAsync("batch-three", [LedgerChecks.WasPersisted(three.Ledger)]);

        Assert.Equal(1, oneResult.Total);
        Assert.Equal(3, threeResult.Total);
    }
}
