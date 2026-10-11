using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Tools;

// 判定型：工具抛异常时，轨迹必须留下失败原因，而不是只剩一个 null 结果
// —— 否则回放轨迹时分不清「工具炸了」和「工具返回了空」。
//
// 负控（必须能红）：删掉 ToolExecutionPipeline 里 FunctionInvoker 的 catch，
// FunctionInvoker_WhenToolThrows_RecordsFailureReason 的第一条断言立刻失败，
// 因为 CompleteCall 在异常路径上根本不会被执行。
public sealed class ToolExecutionPipelineFailureTests
{
    [Fact]
    public async Task FunctionInvoker_WhenToolThrows_RecordsFailureReason()
    {
        var client = new ScriptedChatClient(call => call == 1
            ? ScriptedResponses.ToolCall("boom_tool", ScriptedResponses.Args(("mode", "throw")))
            : ScriptedResponses.Text("done"));
        var run = CreateRun(client);

        var response = await run.Client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "调用失败工具")],
            new ChatOptions { Tools = [BoomTool()] });

        var trace = Assert.Single(run.Traces);
        Assert.Equal("boom_tool", trace.Name);
        Assert.Null(trace.Result);                                        // 没有结果
        Assert.NotNull(trace.Error);                                      // 但有失败原因
        Assert.Contains("工具炸了", trace.Error);                          // 原因里带上了工具抛的消息
        Assert.Contains(nameof(InvalidOperationException), trace.Error);   // 以及异常类型（为后续错误分类留抓手）

        Assert.Equal("done", response.Text);   // 单次工具失败不阻断 run：MEA 转成错误结果回填给模型
        Assert.Equal(2, client.Calls);         // 工具调用 → 回填结果 → 再问模型，共两轮
    }

    [Fact]
    public async Task FunctionInvoker_WhenToolSucceeds_LeavesErrorEmpty()
    {
        var client = new ScriptedChatClient(call => call == 1
            ? ScriptedResponses.ToolCall("echo_tool", ScriptedResponses.Args(("text", "hi")))
            : ScriptedResponses.Text("done"));
        var run = CreateRun(client);

        await run.Client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "调用正常工具")],
            new ChatOptions
            {
                Tools = [AIFunctionFactory.Create((string text) => text, "echo_tool", "原样返回。")]
            });

        var trace = Assert.Single(run.Traces);
        Assert.Equal("echo_tool", trace.Name);
        Assert.Equal("hi", trace.Result);
        Assert.Null(trace.Error);   // 成功步不得带失败原因
    }

    private static ToolExecutionRun CreateRun(IChatClient client)
        => new ToolExecutionPipeline(
                Options.Create(new ToolExecutionOptions()),
                NullLoggerFactory.Instance,
                new ServiceCollection().BuildServiceProvider())
            .Create(client, new ToolExecutionLimits(MaxIterations: 3));

    private static AIFunction BoomTool()
        => AIFunctionFactory.Create(
            (string mode) => Boom(mode), "boom_tool", "总是抛异常，用于验证失败可见性。");

    private static string Boom(string mode)
        => throw new InvalidOperationException($"工具炸了（mode={mode}）");
}
