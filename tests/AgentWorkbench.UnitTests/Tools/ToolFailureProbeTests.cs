using System.Diagnostics;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Tools;

/// <summary>
/// 探针：用 <see cref="FaultInjectorTool"/> 把「工具失败 / 取消」这两条生产路径跑到真实执行环里，
/// 看底座到底是「整轮失败」还是「一次失败不阻断」，以及取消到底有没有穿进工具内部。
///
/// 每个正例都配一个同形状的对照（只动故障旋钮）：对照变绿只能说明旋钮真的在起作用，
/// 否则断言可能只是恒真。
/// </summary>
public sealed class ToolFailureProbeTests
{
    [Fact]
    public async Task ConsecutiveToolFailures_AreRecordedInTrace_AndAbortTheRun()
    {
        var tool = new FaultInjectorTool();
        // 模型「执迷不悟」：每一轮都要求调用同一个坏工具。
        var client = new ScriptedChatClient(_ => CallFaultInjector("throw", failTimes: 99, delayMs: 0));
        var run = CreateRun(client);

        // 连续失败超过执行环容忍上限后，整次请求必须抛错，而不是无限重试或假装成功。
        await Assert.ThrowsAnyAsync<Exception>(async () => await Invoke(run, tool, CancellationToken.None));

        Assert.True(tool.Calls >= 3, $"坏工具应被真实调用至少 3 次，实际 {tool.Calls} 次");
        Assert.Equal(tool.Calls, run.Traces.Count);   // 每一次真实调用都必须留下轨迹
        Assert.All(run.Traces, trace =>
        {
            Assert.Null(trace.Result);                                     // 没有结果
            Assert.NotNull(trace.Error);                                   // 但有失败原因
            Assert.Contains(nameof(InvalidOperationException), trace.Error); // 带异常类型，为后续错误分类留抓手
            Assert.Contains("failTimes=99", trace.Error);                  // 带工具原话
        });
    }

    [Fact]
    public async Task SingleToolFailure_IsStillRecorded_ButDoesNotAbortTheRun()
    {
        var tool = new FaultInjectorTool();
        // 同一形状，只把 failTimes 从 99 拧到 1：一次失败被容忍，run 正常收尾。
        var client = new ScriptedChatClient(call => call == 1
            ? CallFaultInjector("throw", failTimes: 1, delayMs: 0)
            : ScriptedResponses.Text("done"));
        var run = CreateRun(client);

        var response = await Invoke(run, tool, CancellationToken.None);

        Assert.Equal("done", response.Text);
        var trace = Assert.Single(run.Traces);
        Assert.Null(trace.Result);
        Assert.NotNull(trace.Error);   // 失败依然可见；「一次失败」与「连续失败」的区别在这里划清
    }

    [Fact]
    public async Task HealthyTool_LeavesNoError_AndRunCompletes()
    {
        var tool = new FaultInjectorTool();
        var client = new ScriptedChatClient(call => call == 1
            ? CallFaultInjector("throw", failTimes: 0, delayMs: 0)
            : ScriptedResponses.Text("done"));
        var run = CreateRun(client);

        var response = await Invoke(run, tool, CancellationToken.None);

        Assert.Equal("done", response.Text);
        var trace = Assert.Single(run.Traces);
        Assert.Equal("ok", trace.Result);
        Assert.Null(trace.Error);   // 成功步不得带失败原因 —— 否则上面的 Error 断言就没有区分度
    }

    [Fact]
    public async Task SlowTool_ReceivesCancellationInsideTheTool()
    {
        var tool = new FaultInjectorTool();
        var client = new ScriptedChatClient(_ => CallFaultInjector("hang", failTimes: 0, delayMs: 60_000));
        var run = CreateRun(client);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await Invoke(run, tool, cts.Token));
        sw.Stop();

        Assert.Equal(1, tool.Calls);              // 只发起了一次，不然说明环本身在乱跑
        Assert.True(tool.ObservedCancellation);   // 取消真的穿进了工具内部，而不是外层等它跑完再判超时
        Assert.True(sw.ElapsedMilliseconds < 5_000, $"取消应立即生效，实际耗时 {sw.ElapsedMilliseconds}ms");
        Assert.NotNull(run.Traces[0].Error);      // 被取消也留下失败原因，与「返回了空」区分开
    }

    [Fact]
    public async Task SlowTool_WithinTimeoutWindow_ReturnsNormally()
    {
        var tool = new FaultInjectorTool();
        // 同一形状，只把 delayMs 从 60s 拧到 50ms：不取消就该正常返回。
        var client = new ScriptedChatClient(call => call == 1
            ? CallFaultInjector("hang", failTimes: 0, delayMs: 50)
            : ScriptedResponses.Text("done"));
        var run = CreateRun(client);

        var response = await Invoke(run, tool, CancellationToken.None);

        Assert.Equal("done", response.Text);
        Assert.False(tool.ObservedCancellation);
        var trace = Assert.Single(run.Traces);
        Assert.Equal("ok", trace.Result);
        Assert.Null(trace.Error);
    }

    private static ChatResponse CallFaultInjector(string mode, int failTimes, int delayMs)
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", mode), ("failTimes", failTimes), ("delayMs", delayMs)));

    private static Task<ChatResponse> Invoke(
        ToolExecutionRun run, FaultInjectorTool tool, CancellationToken cancellationToken)
        => run.Client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "按脚本调用工具")],
            new ChatOptions { Tools = [.. tool.GetTools()] },
            cancellationToken);

    private static ToolExecutionRun CreateRun(IChatClient client)
        => new ToolExecutionPipeline(
                Options.Create(new ToolExecutionOptions()),
                NullLoggerFactory.Instance,
                new ServiceCollection().BuildServiceProvider())
            .Create(client, new ToolExecutionLimits(MaxIterations: 10));
}
