using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.EvaluationTests.Fixtures;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.EvaluationTests.HarnessReplay;

/// <summary>
/// 阶段 3｜流式事件与轨迹的关系（阶段 1 的收口）。
///
/// 刻意的不对称：<c>ToolCompleted</c> / <c>Completed</c> 事件带<b>完整</b>结果（供界面来源面板），
/// 轨迹只存<b>截断版</b>（<c>MaxAssistantChars = 400</c>，轨迹用于回放/排障，不做全文归档）。
///
/// 本阶段把话说死：<b>回放以轨迹为准（截断版）</b>。若哪天把回放改成读未截断结果，
/// 同一段答复在「截断 / 未截断」之间会产生假红——这里用可判分事实锁住。
/// </summary>
public sealed class TraceTruncationTests
{
    /// <summary>轨迹里 assistant 步的截断长度，与 AgentHarnessService.MaxAssistantChars 对齐。</summary>
    private const int LedgerAssistantLimit = 400;

    /// <summary>答复长度刻意超过截断上限，让「截断是否发生」可判。</summary>
    private const int AnswerChars = 500;

    private static AgentLoopOptions Defaults()
        => new() { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 };

    [Fact]
    public async Task ReplayUsesTruncatedLedger_NotFullStreamingResult()
    {
        var answer = new string('y', AnswerChars);
        var harness = HarnessUnderTest.Create(_ => ScriptedResponses.Text(answer), Defaults());

        // 流式出口：终态事件带回完整答复（界面要给用户看全文）。
        HarnessStreamEvent? terminal = null;
        await foreach (var evt in harness.Harness.StreamAsync(new HarnessRunRequest(null, "长答复流式")))
        {
            if (evt.Kind is HarnessStreamEventKind.Completed or HarnessStreamEventKind.Failed)
                terminal = evt;
        }

        Assert.NotNull(terminal);
        Assert.Equal(HarnessStreamEventKind.Completed, terminal!.Kind);
        Assert.Equal(AnswerChars, terminal.Result!.Text.Length);

        // 轨迹：assistant 步必须截断到上限（大答复不撑爆账本）。
        var trace = Assert.Single(harness.Traces.Saved);
        var assistantStep = Assert.Single(trace.Steps.Where(s => s.Kind == "assistant"));
        Assert.Equal(LedgerAssistantLimit, assistantStep.Result!.Length);

        // 回放：以轨迹为准 → 只看到截断版，而不是流式事件里的完整版。
        var replayed = await TraceReplay.ReadTextAsync(harness.Harness, terminal.Result.RunId);
        Assert.Equal(LedgerAssistantLimit, replayed.Length);
        Assert.Equal(answer[..LedgerAssistantLimit], replayed);
    }

    [Fact]
    public async Task NonStreamingRun_AlsoTruncatesLedger_AndReplaysTruncated()
    {
        // 非流式出口与流式出口同源：同一条截断语义，不因出口不同而分叉。
        var answer = new string('z', AnswerChars);
        var harness = HarnessUnderTest.Create(_ => ScriptedResponses.Text(answer), Defaults());

        var outcome = await harness.RunAsync("长答复非流式");

        var trace = Assert.Single(harness.Traces.Saved);
        var assistantStep = Assert.Single(trace.Steps.Where(s => s.Kind == "assistant"));
        Assert.Equal(LedgerAssistantLimit, assistantStep.Result!.Length);

        var replayed = await TraceReplay.ReadTextAsync(harness.Harness, outcome.Result.RunId);
        Assert.Equal(LedgerAssistantLimit, replayed.Length);
    }
}
