using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.Observability;
using AgentWorkbench.Shared;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.Evaluation.AgentTask.HarnessStreaming;

/// <summary>
/// 阶段 1「三条路并一条」的验收：流式出口必须与非流式出口<b>同源</b>——
/// 同一 loop、同一执行控制、同一轨迹落库，差别只在产出形态。
///
/// 为什么单独立域：此前用户在用的聊天页绕过 harness 直连对话服务，那条路既没有轨迹也没有预算，
/// 评测一条都覆盖不到。本文件把「流式出口也进同一个账本」变成可判分事实——
/// 任何「给流式另写一套收尾」的改动都会在这里变红（历史版本正是这样分叉的）。
/// </summary>
public sealed class HarnessStreamingTests
{
    private static AgentLoopOptions Defaults()
        => new() { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 3 };

    /// <summary>第 1 轮调工具、第 2 轮收口的健康脚本。</summary>
    private static ChatResponse HealthyToolCall()
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", "throw"), ("failTimes", 0), ("delayMs", 0)));

    /// <summary>把一次流式 run 的事件全部收下来（顺序即产出顺序）。</summary>
    private static async Task<List<HarnessStreamEvent>> CollectAsync(
        IAgentHarness harness, HarnessRunRequest request, CancellationToken cancellationToken = default)
    {
        var events = new List<HarnessStreamEvent>();
        await foreach (var evt in harness.StreamAsync(request, cancellationToken))
            events.Add(evt);
        return events;
    }

    /// <summary>
    /// 事件流必须以<b>唯一</b>终态（Completed / Failed）收尾，且它就是最后一个事件。
    /// 抽成判据，是为了让「终态缺失 / 重复 / 不在末尾」三种坏形状都能被同一个断言抓住。
    /// </summary>
    private static HarnessStreamEvent AssertSingleTerminalEvent(IReadOnlyList<HarnessStreamEvent> events)
    {
        var terminals = events
            .Where(e => e.Kind is HarnessStreamEventKind.Completed or HarnessStreamEventKind.Failed)
            .ToList();

        Assert.True(terminals.Count == 1,
            $"事件流必须以唯一终态收尾，实际终态 {terminals.Count} 个（事件总数 {events.Count}）");
        Assert.True(events[^1].Kind is HarnessStreamEventKind.Completed or HarnessStreamEventKind.Failed,
            "终态必须是最后一个事件——终态之后再吐事件，说明收尾与产出没排好序");
        return terminals[0];
    }

    private static string[] ToolNames(ExecutionTrace trace)
        => trace.Steps.Where(s => s.Kind == "tool").Select(s => s.Tool ?? string.Empty).ToArray();

    [Fact]
    public async Task Streaming_EmitsTextDeltas_ToolEvent_AndSingleSucceededTerminal()
    {
        // 答复刻意长于夹具的切片长度（4 字符），否则「分片」这条断言恒等于 1 片、测不到累积。
        const string Answer = "完成得很好，这是一段足够长的答复。";

        var harness = HarnessUnderTest.Create(
            call => call == 1 ? HealthyToolCall() : ScriptedResponses.Text(Answer), Defaults());

        var events = await CollectAsync(harness.Harness, new HarnessRunRequest(null, "按脚本调一次工具再收口"));

        // ① 增量：脚本化模型按 4 字符切片，必须真的分片到达，且拼起来等于完整答复。
        // 只有段数 > 1，「累积」这条逻辑才算被走到（收到一整段的话它恒等于最终答复）。
        var deltas = events
            .Where(e => e.Kind == HarnessStreamEventKind.TextDelta)
            .Select(e => e.Text)
            .ToList();
        Assert.True(deltas.Count > 1, $"流式必须真的分片到达，实际 {deltas.Count} 片");
        Assert.Equal(Answer, string.Concat(deltas));

        // ② 工具完成事件：工具名在、成功调用不带 Error，且工具确实被执行了一次（不是纸面事件）。
        var toolEvent = Assert.Single(events, e => e.Kind == HarnessStreamEventKind.ToolCompleted);
        Assert.Equal(FaultInjectorTool.ToolName, toolEvent.Tool!.Name);
        Assert.Null(toolEvent.Tool.Error);
        Assert.Equal(1, harness.Tool.Calls);

        // ③ 终态唯一、在末尾、带完整结果与运行标识（运行标识就是回放入口）。
        var terminal = AssertSingleTerminalEvent(events);
        Assert.Equal(HarnessStreamEventKind.Completed, terminal.Kind);
        Assert.Equal(Answer, terminal.Result!.Text);
        Assert.Equal(AgentRunStatus.Succeeded, terminal.Result.Status);
        Assert.True(terminal.Result.RunId >= 1, "终态必须带回运行标识——它是回放的入口");

        // ④ 账本：流式出口与非流式共用一处收尾，工具步与答复步都必须留下。
        var trace = Assert.Single(harness.Traces.Saved);
        Assert.Equal(AgentRunStatus.Succeeded, trace.Status);
        Assert.Contains(trace.Steps, s => s.Kind == "tool" && s.Tool == FaultInjectorTool.ToolName);
        Assert.Contains(trace.Steps, s => s.Kind == "assistant" && s.Result == Answer);
    }

    [Fact]
    public async Task Streaming_FailedRun_EmitsNoCompletedEvent_NegativeControl()
    {
        // 负控：模型侧一上来就抛。此时事件流里绝不允许出现 Completed——
        // 一旦出现就是把失败报成成功（伪造终态），这是流式出口最危险的假绿。
        var harness = HarnessUnderTest.Create(
            _ => throw new InvalidOperationException("故障注入：模型侧崩溃"), Defaults());

        var events = await CollectAsync(harness.Harness, new HarnessRunRequest(null, "必失败的 run"));

        var terminal = AssertSingleTerminalEvent(events);
        Assert.Equal(HarnessStreamEventKind.Failed, terminal.Kind);
        Assert.False(string.IsNullOrWhiteSpace(terminal.Error), "失败终态必须带上给用户看的错误描述");
        Assert.DoesNotContain(events, e => e.Kind == HarnessStreamEventKind.Completed);

        // 失败也要留痕：流式出口没有「悄悄挂掉、不留账」这条路。
        var trace = Assert.Single(harness.Traces.Saved);
        Assert.Equal(AgentRunStatus.Failed, trace.Status);
    }

    [Fact]
    public void TerminalEventCheck_HasTeeth_NegativeControl()
    {
        // 判据自身的负控：缺终态、终态不在末尾、终态重复——三种坏形状都必须抛。
        // 少了这一段，「AssertSingleTerminalEvent 全绿」可能只是它根本不检查（假门禁）。
        Assert.ThrowsAny<Exception>(() => AssertSingleTerminalEvent(
            [new HarnessStreamEvent(HarnessStreamEventKind.TextDelta, Text: "半截回答")]));

        Assert.ThrowsAny<Exception>(() => AssertSingleTerminalEvent(
        [
            new HarnessStreamEvent(HarnessStreamEventKind.Completed),
            new HarnessStreamEvent(HarnessStreamEventKind.TextDelta, Text: "终态之后又冒出一条")
        ]));

        Assert.ThrowsAny<Exception>(() => AssertSingleTerminalEvent(
        [
            new HarnessStreamEvent(HarnessStreamEventKind.Completed),
            new HarnessStreamEvent(HarnessStreamEventKind.Failed, Error: "两个终态")
        ]));
    }

    [Fact]
    public async Task StreamingAndNonStreaming_LeaveSameLedgerFacts_AndSameToolCalls()
    {
        // 「三条路并一条」的核心承诺：两条出口共用一处准备、一处收尾，账本必须逐项对齐。
        // 若有人给流式另写一套收尾，这里立刻红。
        static HarnessUnderTest Build() => HarnessUnderTest.Create(
            call => call == 1 ? HealthyToolCall() : ScriptedResponses.Text("done"), Defaults());

        var sync = Build();
        var syncOutcome = await sync.RunAsync("同一脚本，非流式出口");

        var stream = Build();
        var events = await CollectAsync(stream.Harness, new HarnessRunRequest(null, "同一脚本，流式出口"));

        var syncTrace = Assert.Single(sync.Traces.Saved);
        var streamTrace = Assert.Single(stream.Traces.Saved);

        Assert.Equal(syncTrace.Status, streamTrace.Status);
        Assert.Equal(syncTrace.Iterations, streamTrace.Iterations);
        Assert.Equal(ToolNames(syncTrace), ToolNames(streamTrace));
        Assert.Equal(sync.Tool.Calls, stream.Tool.Calls);
        Assert.Equal(sync.Client.Calls, stream.Client.Calls);
        Assert.Equal(syncOutcome.Result.Text, AssertSingleTerminalEvent(events).Result!.Text);
    }

    [Fact]
    public async Task ToolEventCarriesFullResult_WhileLedgerKeepsTruncatedResult()
    {
        // 「事件完整 / 账本截断」是刻意的不对称：大结果撑爆账本没有价值，
        // 但界面（知识库来源面板）必须拿到完整内容。断言同时锁住两侧——改单边即红。
        var longTool = new LongResultTool();
        var harness = HarnessUnderTest.Create(
            call => call == 1
                ? ScriptedResponses.ToolCall(LongResultTool.ToolName, ScriptedResponses.Args())
                : ScriptedResponses.Text("done"),
            Defaults(),
            extraTools: [.. longTool.GetTools()]);

        var events = await CollectAsync(harness.Harness, new HarnessRunRequest(null, "取一段长文本"));

        var toolEvent = Assert.Single(events, e => e.Kind == HarnessStreamEventKind.ToolCompleted);
        Assert.Equal(LongResultTool.ToolName, toolEvent.Tool!.Name);
        Assert.Equal(LongResultTool.ResultChars, toolEvent.Tool.Result!.Length);

        var step = Assert.Single(harness.Traces.Saved).Steps.Single(s => s.Kind == "tool");
        Assert.Equal(new ToolExecutionOptions().MaxResultChars, step.Result!.Length);
    }
}
