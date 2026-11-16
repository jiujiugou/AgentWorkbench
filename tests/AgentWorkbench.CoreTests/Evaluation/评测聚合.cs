using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentWorkbench.Evaluation.Kernel;
using AgentWorkbench.Observability;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Shared;
using Xunit;

namespace AgentWorkbench.CoreTests.Evaluation;

/// <summary>
/// 聚合层（<see cref="KernelSample"/> + <see cref="KernelReport"/>）的判据。
///
/// <b>为什么这一层能单测</b>：它不跑模型、不读存储，只对一组数字做计算。
/// 「跑 N 次」归调用方（假模型验算法、真模型验分布），所以这里喂手写样本就够，
/// 不需要启动 Harness。
///
/// <b>每条都写负控</b>：只验「正常情况算得对」，一条把均值算反的实现也能过。
/// </summary>
public sealed class 评测聚合
{
    // 负控：把 From 的守卫删掉、返回全零报告 → 本条红。
    [Fact]
    public void 空采样直接拒绝而不是给一份全通过的报告()
    {
        var ex = Assert.Throws<ArgumentException>(() => KernelReport.From(Array.Empty<KernelSample>()));
        Assert.Contains("采样为空", ex.Message, StringComparison.Ordinal);
    }

    // 负控：把 PassRate 改成 passed/10（写死分母）→ 本条红。
    [Fact]
    public void 通过率按次数算而不是按判据条数算()
    {
        var report = KernelReport.From(
        [
            Sample(runId: 1, passed: true),
            Sample(runId: 2, passed: true),
            Sample(runId: 3, passed: true),
            Sample(runId: 4, passed: false),
            Sample(runId: 5, passed: false),
        ]);

        Assert.Equal(5, report.Runs);
        Assert.Equal(3, report.Passed);
        Assert.Equal(0.6, report.PassRate, 3);
    }

    // 负控：把 Percentile 改成取平均值或取最后一个 → 本条红。
    [Fact]
    public void 耗时取最近秩的p95()
    {
        // 10 个样本，耗时 100..1000。最近秩 ceil(0.95*10)-1 = 9 → 最大值。
        var samples = Enumerable.Range(1, 10)
            .Select(i => Sample(runId: i, latencyMs: i * 100))
            .ToArray();

        var report = KernelReport.From(samples);

        Assert.Equal(1_000, report.P95LatencyMs);
        Assert.Equal(550, report.AvgLatencyMs, 3);
    }

    // 负控：把 Tally 的 null 过滤删掉 → StopKinds 会多出一个名为空的桶，本条红。
    [Fact]
    public void 停因与终态分别计数且忽略空值()
    {
        var samples = new[]
        {
            Sample(runId: 1, stopKind: "repeated_tool_calls"),
            Sample(runId: 2, stopKind: "repeated_tool_calls"),
            Sample(runId: 3, stopKind: "repeated_tool_calls"),
            Sample(runId: 4, stopKind: "token_budget"),
            Sample(runId: 5, stopKind: null),
        };

        var report = KernelReport.From(samples);

        Assert.Equal(2, report.StopKinds.Count);
        Assert.Equal(3, report.StopKinds["repeated_tool_calls"]);
        Assert.Equal(1, report.StopKinds["token_budget"]);
        Assert.DoesNotContain(string.Empty, report.StopKinds.Keys);
    }

    // 负控：把缺失轨迹的 0 也放进均值 → AvgIterations 变成 2.4，本条红。
    [Fact]
    public void 缺失轨迹不计入数字均值但仍计入失败()
    {
        var samples = new[]
        {
            Sample(runId: 1, passed: true, iterations: 2),
            Sample(runId: 2, passed: true, iterations: 4),
            Sample(runId: 3, passed: true, iterations: 6),
            // 读不回轨迹：判据必然红，数字全是 0，不能污染均值。
            Sample(runId: 4, passed: false, hasTrace: false, iterations: 0, latencyMs: 0),
            Sample(runId: 5, passed: false, hasTrace: false, iterations: 0, latencyMs: 0),
        };

        var report = KernelReport.From(samples);

        Assert.Equal(5, report.Runs);
        Assert.Equal(2, report.MissingTrace);
        Assert.Equal(4, report.AvgIterations, 3);
        Assert.Equal(2, report.MinIterations);
        Assert.Equal(6, report.MaxIterations);
        Assert.Equal(0.6, report.PassRate, 3);
    }

    // 负控：把「全部未上报」写成 0 而不是 null → 本条红。
    [Fact]
    public void 用量全部未上报时是null而不是零()
    {
        var report = KernelReport.From(
        [
            Sample(runId: 1, input: null, output: null),
            Sample(runId: 2, input: null, output: null),
        ]);

        Assert.Null(report.AvgInputTokens);
        Assert.Null(report.AvgOutputTokens);
    }

    // 负控：把 null 当 0 参与均值 → 本条算出 10，断言 15 会红。
    [Fact]
    public void 用量部分上报时只对上报的求均值()
    {
        var report = KernelReport.From(
        [
            Sample(runId: 1, input: 10, output: 4),
            Sample(runId: 2, input: 20, output: 6),
            Sample(runId: 3, input: null, output: null),
        ]);

        Assert.Equal(15, report.AvgInputTokens);
        Assert.Equal(5, report.AvgOutputTokens);
    }

    // 负控：让 KernelSample.From 去读 run.Iterations（内存对象）而不是 trace.Iterations → 本条红。
    [Fact]
    public async Task 采样从落库轨迹取值而不是从内存对象()
    {
        // run 里是 1 轮，轨迹里是 5 轮：两者不一致时必须以轨迹为准。
        var trace = Trace(iterations: 5, latencyMs: 800, input: 33, output: 7);
        var results = await KernelEvaluation.EvaluateAsync(
            Result(), "评测", trace, t => [KernelChecks.Iterations(t, 5)]);

        var sample = KernelSample.From(Result(), trace, results);

        Assert.True(sample.HasTrace);
        Assert.True(sample.AllPassed);
        Assert.Equal(5, sample.Iterations);
        Assert.Equal(800, sample.LatencyMs);
        Assert.Equal(33, sample.InputTokens);
        Assert.Equal(7, sample.OutputTokens);
    }

    // 负控：让 From 在 trace 为 null 时仍报 HasTrace=true → 本条红（缺失会被算进均值）。
    [Fact]
    public async Task 读不回轨迹时采样标记缺失且数字归零()
    {
        var results = await KernelEvaluation.EvaluateAsync(
            Result(), "评测", (ExecutionTrace?)null, t => [KernelChecks.Iterations(t, 1)]);

        var sample = KernelSample.From(Result(), null, results);

        Assert.False(sample.HasTrace);
        Assert.False(sample.AllPassed);
        Assert.Equal(0, sample.Iterations);
        Assert.Null(sample.InputTokens);
    }

    // ── 构造 ────────────────────────────────────────────────────────

    private static KernelSample Sample(
        long runId,
        bool passed = true,
        bool hasTrace = true,
        int iterations = 1,
        int toolSteps = 1,
        long latencyMs = 100,
        string status = AgentRunStatus.Succeeded,
        string? stopKind = null,
        long? input = 10,
        long? output = 5)
        => new(runId, passed, hasTrace, iterations, toolSteps, latencyMs, status, stopKind, input, output);

    private static HarnessRunResult Result()
        => new(1, "conv-1", "答完了", 1, "gpt-test", 100,
            [new HarnessStep(0, "assistant", null, null, "答完了")], AgentRunStatus.Succeeded);

    private static ExecutionTrace Trace(
        int iterations = 1,
        long latencyMs = 100,
        string status = AgentRunStatus.Succeeded,
        RunStopDetail? stop = null,
        long? input = 10,
        long? output = 5,
        params ExecutionStep[] steps)
        => new("conv-1", status, iterations, "gpt-test", latencyMs, null, null,
            steps.Length == 0 ? [new ExecutionStep(0, "assistant", null, null, "答完了")] : steps,
            input, output, true, stop);
}
