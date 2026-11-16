using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentWorkbench.Evaluation.Kernel;
using AgentWorkbench.Observability;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Shared;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentWorkbench.CoreTests.Evaluation;

/// <summary>
/// <see cref="KernelChecks"/> 里「补全字段覆盖」那七条判据的验收：每条都要能绿、也要能红。
///
/// <b>为什么单独一个文件</b>：<c>评测机制.cs</c> 验的是「闭环成立」（投影 → 评测 → 读回），
/// 本文件验的是「判据本身敏不敏感」。两者混在一起时，一条判据恒真会被闭环的绿盖住。
///
/// <b>每条都写了负控</b>：只断言「正例能过」等于没验——恒真的判据也满足那一半。
///
/// <b>层 × 方向</b>：只有 Core 的轨迹形状，不碰存储、不碰 Harness 装配。
/// </summary>
public sealed class 内核判据字段覆盖
{
    // 负控：把 SessionOwnership 改成忽略入参（恒真）→ 本条红。
    [Fact]
    public async Task 会话归属判据_串号会红()
    {
        var pass = await Eval(t => [KernelChecks.SessionOwnership(t, "conv-1")], Trace());
        Assert.True(pass.AllPassed);

        var fail = await Eval(t => [KernelChecks.SessionOwnership(t, "conv-2")], Trace());
        Assert.False(fail.AllPassed, "轨迹记到别人的会话上必须红");
    }

    // 负控：把 ModelUsed 的 string.Equals 改成恒真 → 本条红。
    [Fact]
    public async Task 模型判据_换名会红()
    {
        var pass = await Eval(t => [KernelChecks.ModelUsed(t, "gpt-test")], Trace());
        Assert.True(pass.AllPassed);

        var fail = await Eval(t => [KernelChecks.ModelUsed(t, "other-model")], Trace());
        Assert.False(fail.AllPassed);

        // null 与空名是两回事：准备阶段就失败时模型字段为 null，不能和「用了一个空名」混同。
        var noModel = await Eval(t => [KernelChecks.ModelUsed(t, null)], Trace(model: null));
        Assert.True(noModel.AllPassed);
    }

    // 负控：把 ElapsedWithin 改成只判下界（忽略 maxMs）→ 第二条红。
    [Fact]
    public async Task 耗时判据_区间外会红()
    {
        // 实际耗时为 120ms。
        var pass = await Eval(t => [KernelChecks.ElapsedWithin(t, 0, 1_000)], Trace());
        Assert.True(pass.AllPassed);

        var tooSlow = await Eval(t => [KernelChecks.ElapsedWithin(t, 0, 100)], Trace());
        Assert.False(tooSlow.AllPassed, "超过上界必须红");

        var tooFast = await Eval(t => [KernelChecks.ElapsedWithin(t, 200)], Trace());
        Assert.False(tooFast.AllPassed, "低于下界必须红——瞬间返回却报成功是明显的假执行");
    }

    // 负控：把 ErrorNarrative 改成只看 status（不看 Error 是否为空）→ 前两条之一必红。
    [Fact]
    public async Task 错误人话判据_成功却带错会红()
    {
        var okSilent = await Eval(t => [KernelChecks.ErrorNarrative(t, false)], Trace());
        Assert.True(okSilent.AllPassed);

        var failedWithText = await Eval(
            t => [KernelChecks.ErrorNarrative(t, true)],
            Trace(status: AgentRunStatus.Failed, error: "上游 500"));
        Assert.True(failedWithText.AllPassed);

        var okButNoisy = await Eval(
            t => [KernelChecks.ErrorNarrative(t, false)],
            Trace(status: AgentRunStatus.Succeeded, error: "不该出现的错误串"));
        Assert.False(okButNoisy.AllPassed, "成功却带错误人话必须红");

        var failedButSilent = await Eval(
            t => [KernelChecks.ErrorNarrative(t, true)],
            Trace(status: AgentRunStatus.Failed, error: null));
        Assert.False(failedButSilent.AllPassed, "失败却没有人话必须红——用户看不出是出错");
    }

    // 负控：把 ToolStep 的「找不到该步」改成跳过 → 第三条红。
    [Fact]
    public async Task 工具步判据_换工具名与缺步都会红()
    {
        var withTool = Trace(steps: new ExecutionStep(1, "tool", "Echo", "{}", "ok"));
        var pass = await Eval(t => [KernelChecks.ToolStep(t, 1, "Echo")], withTool);
        Assert.True(pass.AllPassed);

        var wrongTool = await Eval(t => [KernelChecks.ToolStep(t, 1, "Other")], withTool);
        Assert.False(wrongTool.AllPassed);

        var missingStep = await Eval(t => [KernelChecks.ToolStep(t, 2, "Echo")], withTool);
        Assert.False(missingStep.AllPassed, "找不到该轮必须红，不能跳过");

        // 工具抛异常时，要能读出是哪一件、抛了什么。
        var thrown = Trace(steps: new ExecutionStep(1, "tool", "Echo", "{}", null, "InvalidOperationException: boom"));
        var errPass = await Eval(t => [KernelChecks.ToolStep(t, 1, "Echo", "InvalidOperationException")], thrown);
        Assert.True(errPass.AllPassed);

        var errMiss = await Eval(t => [KernelChecks.ToolStep(t, 1, "Echo", "TimeoutException")], thrown);
        Assert.False(errMiss.AllPassed);

        // 反向：期望「无异常」而实际有异常，也必须红。
        var shouldBeClean = await Eval(t => [KernelChecks.ToolStep(t, 1, "Echo")], thrown);
        Assert.False(shouldBeClean.AllPassed, "期望无异常而实际抛了，必须红");
    }

    // 负控：把 ToolPayload 改成只判「非空」→ 第二、三条红。
    [Fact]
    public async Task 工具载荷判据_参数或结果被改写会红()
    {
        var step = new ExecutionStep(1, "tool", "Echo", """{"text":"hi"}""", "echo:hi");
        var trace = Trace(steps: step);

        var pass = await Eval(t => [KernelChecks.ToolPayload(t, 1, "hi", "echo:hi")], trace);
        Assert.True(pass.AllPassed);

        var argChanged = await Eval(t => [KernelChecks.ToolPayload(t, 1, "bye")], trace);
        Assert.False(argChanged.AllPassed, "参数被改写必须红");

        var resultChanged = await Eval(t => [KernelChecks.ToolPayload(t, 1, null, "other")], trace);
        Assert.False(resultChanged.AllPassed, "结果被改写必须红");
    }

    // 负控：把 StepKinds 改成只比数量 → 第二条红。
    [Fact]
    public async Task 步骤序列判据_顺序与数量不符都会红()
    {
        var trace = Trace(steps:
        [
            new ExecutionStep(1, "tool", "Echo", "{}", "ok"),
            new ExecutionStep(2, "assistant", null, null, "答完了"),
        ]);

        var pass = await Eval(t => [KernelChecks.StepKinds(t, "tool", "assistant")], trace);
        Assert.True(pass.AllPassed);

        var swapped = await Eval(t => [KernelChecks.StepKinds(t, "assistant", "tool")], trace);
        Assert.False(swapped.AllPassed, "顺序换了必须红");

        var shortened = await Eval(t => [KernelChecks.StepKinds(t, "tool")], trace);
        Assert.False(shortened.AllPassed, "数量不符必须红");
    }

    // 统一约定：读不回轨迹时一条都不能跳过。跳过会让「存储挂了」显示成满分。
    // 逐条跑而不是一次跑七条：聚合结果的 Failed 是「失败的评测项数」（这里恒为 1），
    // 拿它判「七条都红」会假绿——一条红和七条红都得到 1。
    [Fact]
    public async Task 七条新判据_读不回轨迹时全部红()
    {
        Func<ExecutionTrace?, IEnumerable<EvalCheck>>[] factories =
        [
            _ => [KernelChecks.SessionOwnership(null, "conv-1")],
            _ => [KernelChecks.ModelUsed(null, "gpt-test")],
            _ => [KernelChecks.ElapsedWithin(null, 0)],
            _ => [KernelChecks.ErrorNarrative(null, false)],
            _ => [KernelChecks.ToolStep(null, 1, "Echo")],
            _ => [KernelChecks.ToolPayload(null, 1, "x")],
            _ => [KernelChecks.StepKinds(null, "assistant")],
        ];

        foreach (var factory in factories)
        {
            var results = await KernelEvaluation.EvaluateAsync(Result(), "评测", (ExecutionTrace?)null, factory);
            Assert.False(results.AllPassed, "读不回轨迹时判据必须红，不能跳过");
        }
    }

    // ── 构造 ────────────────────────────────────────────────────────

    private static Task<AgentEvaluationResults> Eval(
        Func<ExecutionTrace?, IEnumerable<EvalCheck>> checks, ExecutionTrace? trace)
        => KernelEvaluation.EvaluateAsync(Result(), "评测", trace, checks);

    private static HarnessRunResult Result()
        => new(7, "conv-1", "答完了", 1, "gpt-test", 120,
            [new HarnessStep(0, "assistant", null, null, "答完了")], AgentRunStatus.Succeeded);

    private static ExecutionTrace Trace(
        string sessionId = "conv-1",
        string status = AgentRunStatus.Succeeded,
        string? code = null,
        string? model = "gpt-test",
        long latencyMs = 120,
        string? error = null,
        params ExecutionStep[] steps)
        => new(sessionId, status, 1, model, latencyMs, error, code,
            steps.Length == 0 ? [new ExecutionStep(0, "assistant", null, null, "答完了")] : steps);
}
