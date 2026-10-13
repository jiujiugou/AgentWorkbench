using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.EvaluationTests.Fixtures;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.EvaluationTests.HarnessInstructions;

/// <summary>
/// 指令通道（run 级系统指令）的判据，重点是**它不能把执行控制绕过去**。
///
/// 背景（实测踩过的坑）：指令曾改用 MEA 的 <c>AIContextProviders</c> 注入，结果 MEA 在
/// 「我们自己装配的函数调用环」外面又包了一层，<c>ToolExecutionPipeline</c> 的守卫与轨迹采集全被绕过——
/// 连续同参调用不再判 <c>loop_detected</c>、工具异常不再归类 <c>tool_error</c>，全部退化成 <c>succeeded</c>。
/// 现在指令走 <see cref="RunInstructionsChatClient"/>（装饰在环外面，只往消息前插一条 system）。
/// 这些用例就是那个坑的回归闸：谁再把指令挪回 provider，这里立刻红。
/// </summary>
public sealed class RunInstructionsTests
{
    private const string Instructions = "你是严谨的资料问答助手，只能依据资料作答。";

    private static AgentLoopOptions Defaults()
        => new() { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 3 };

    private static ChatResponse HealthyToolCall()
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", "throw"), ("failTimes", 0), ("delayMs", 0)));

    [Fact]
    public async Task Instructions_ReachTheModel_AsSystemMessage_OnEveryModelRequest()
    {
        var harness = HarnessUnderTest.Create(
            call => call == 1 ? HealthyToolCall() : ScriptedResponses.Text("done"), Defaults());

        await harness.Harness.RunAsync(new HarnessRunRequest(null, "手册里 X 怎么写？", Instructions));

        Assert.Equal(2, harness.Client.Requests.Count);

        // 每一轮都要带上指令：工具轮之后的收口轮如果丢了指令，引用规范就只在第一轮生效。
        foreach (var request in harness.Client.Requests)
        {
            var system = Assert.Single(request.Where(m => m.Role == ChatRole.System));
            Assert.Equal(Instructions, system.Text);
        }

        // 注入的位置在最前，且不影响用户原文照常进模型。
        Assert.Equal(ChatRole.System, harness.Client.Requests[0][0].Role);
        Assert.Equal("手册里 X 怎么写？", harness.Client.Requests[0][^1].Text);
    }

    [Fact]
    public async Task NoInstructions_MeansNoSystemMessage_NegativeControl()
    {
        // 负控：不带指令时不该凭空多出系统消息——否则上一条用例的断言没有区分度
        // （「messages[0] 是 system」可能只是链路上恒有一条，而不是装饰器真的按 run 注入）。
        var harness = HarnessUnderTest.Create(
            call => call == 1 ? HealthyToolCall() : ScriptedResponses.Text("done"), Defaults());

        await harness.Harness.RunAsync(new HarnessRunRequest(null, "普通对话"));

        Assert.DoesNotContain(harness.Client.Requests.SelectMany(r => r), m => m.Role == ChatRole.System);
    }

    [Fact]
    public async Task Instructions_DoNotBypassLoopDetection()
    {
        // 核心回归闸：带指令时执行控制必须照常生效。
        // 用 AIContextProviders 注入的那一版在这里会得到 succeeded（守卫根本没被调用）。
        var harness = HarnessUnderTest.Create(_ => HealthyToolCall(), Defaults());

        var outcome = await harness.Harness.RunAsync(
            new HarnessRunRequest(null, "同一调用反复来", Instructions));

        Assert.Equal(AgentRunStatus.LoopDetected, outcome.Status);
        Assert.Equal(AgentRunCode.LoopDetected, outcome.Code);

        // 轨迹里仍然看得到工具步——说明采集层没被绕过。
        var trace = Assert.Single(harness.Traces.Saved);
        Assert.Contains(trace.Steps, s => s.Kind == "tool" && s.Tool == FaultInjectorTool.ToolName);
    }

    [Fact]
    public async Task Instructions_DoNotBypassToolFailureClassification()
    {
        // 同一个坑的另一面：工具连续失败仍必须归类 tool_error，而不是被外层环吞成 succeeded/unhandled。
        // 循环检测关掉（0）——否则同一调用重复三次会先被判 loop_detected 干净收口，走不到失败路径。
        var harness = HarnessUnderTest.Create(
            _ => ScriptedResponses.ToolCall(
                FaultInjectorTool.ToolName,
                ScriptedResponses.Args(("mode", "throw"), ("failTimes", 99), ("delayMs", 0))),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 });

        var failure = await harness.RunExpectingFailureAsync("工具一直失败，带指令");

        // 抛什么类型由框架决定（可能是 AggregateException），不动这个断言；
        // 要证的是「归类没被绕过」——状态与归类码一起看，加上每一步留下了失败原因。
        Assert.NotNull(failure.Exception);
        var trace = Assert.Single(harness.Traces.Saved);
        Assert.Equal(AgentRunStatus.Failed, trace.Status);
        Assert.Equal(AgentRunCode.ToolError, trace.ErrorCode);

        var toolSteps = trace.Steps.Where(s => s.Kind == "tool").ToList();
        Assert.NotEmpty(toolSteps);
        Assert.All(toolSteps, s => Assert.NotNull(s.Error));
    }
}
