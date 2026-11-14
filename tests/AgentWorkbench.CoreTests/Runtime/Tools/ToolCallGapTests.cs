using System;
using System.Collections.Generic;
using System.Linq;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.TestKit;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.CoreTests.Runtime.Tools;

// 只验框架没给、本仓补的三格：结果进模型前的上界、调用前按结构拦下畸形参数、
// 以及工具异常仍被转成结果消息回填（复验，不新写实现）。三条均已落地且全绿。
// 每条先给绿对照，防止断言恒真；改坏对应实现会让断言转红。
public class ToolCallGapTests
{
    // 负控：把 ToolExecutionPipeline 的 `return Truncate(text);` 改回 `return result;` → 本条红。
    [Fact]
    public async Task 结果进模型前有内容上界_轨迹截断作绿对照()
    {
        const int bound = 120;
        var payload = new string('A', 600);
        var tool = CountingTool.NoArg("search", () => payload);
        var inner = new ScriptedChatClient().ToolCall("search").Reply("完成");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto),
            maxResultChars: bound);

        Assert.Equal(2, inner.CallCount);
        Assert.Equal(1, tool.Executions);
        Assert.Equal(bound, (result.Run.Traces[0].Result?.ToString() ?? string.Empty).Length);

        var seen = 结果文本(inner.LastRequest);
        Assert.True(seen.Contains(payload[..bound], StringComparison.Ordinal),
            "模型请求里找不到工具结果，断言会变成恒真");
        Assert.False(seen.Contains(payload[..(bound + 1)], StringComparison.Ordinal),
            $"模型收到了超过上界 {bound} 的结果：当前只截了轨迹，回填仍是全文");
    }

    // 负控：在 InvokeAsync 之前加按结构校验，畸形参数回填 argument_validation_failed 且不进执行体后转绿。
    [Fact]
    public async Task 畸形参数在调用前被挡下并回填可归因拒绝码_合法参数作绿对照()
    {
        var okTool = CountingTool.OneArg("calc", input => "结果:" + input);
        var okInner = new ScriptedChatClient()
            .ToolCall("calc", new Dictionary<string, object?> { ["input"] = "1+2" })
            .Reply("完成");
        var ok = await PipelineDriver.DriveAsync(
            okInner, okTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal(1, okTool.Executions);
        Assert.Null(ok.Run.Traces[0].Error);

        var badTool = CountingTool.OneArg("calc", _ => "不该被执行");
        var badInner = new ScriptedChatClient()
            .ToolCall("calc", new Dictionary<string, object?> { ["input"] = 12345 })
            .Reply("换路");
        var bad = await PipelineDriver.DriveAsync(
            badInner, badTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.NotEmpty(bad.Run.Traces);
        Assert.Equal("argument_validation_failed", bad.Run.Traces[0].Error);
        Assert.Equal(0, badTool.Executions);
    }

    // H3 复验：框架机制「工具异常 → 结果消息回填、不炸整轮」在本仓替换 FunctionInvoker 后是否仍成立。
    // 只复验、不实现。负控：把装配里的 FunctionInvokingChatClient 拿掉（异常直接向上冒），
    // 或让 catch 之外再包一层把异常抛给调用方，模型就不会被再问一次，此测试变红。
    // 注意：轨迹 Error 与 ToolFailures 是本仓自写的留痕，即使异常冒泡也成立，不能拿它们当回填的证据。
    [Fact]
    public async Task 工具异常仍被转成结果消息回填且不炸整轮_正常工具作绿对照()
    {
        // 绿对照：同一装配下，正常工具的结果确实会回填并让模型被再问一次。
        var okTool = CountingTool.NoArg("ok", () => "正常结果");
        var okInner = new ScriptedChatClient().ToolCall("ok").Reply("完成");
        var ok = await PipelineDriver.DriveAsync(
            okInner, okTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Null(ok.Thrown);
        Assert.Equal(2, okInner.CallCount);
        Assert.True(okInner.LastRequest.SelectMany(m => m.Contents).Any(c => c is FunctionResultContent),
            "正常工具的结果没有作为结果消息回到模型，装置本身不成立");

        // 被测：工具抛异常后是否仍走同一条回填路径。
        var boom = CountingTool.NoArg("boom", () => throw new InvalidOperationException("工具内部炸了"));
        var inner = new ScriptedChatClient().ToolCall("boom").Reply("收到失败，换路");
        var result = await PipelineDriver.DriveAsync(
            inner, boom.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal(1, boom.Executions);
        Assert.Single(result.Run.ToolFailures);
        Assert.NotEmpty(result.Run.Traces);
        Assert.Contains("InvalidOperationException", result.Run.Traces[0].Error ?? string.Empty);

        Assert.True(result.Thrown is null,
            $"异常冒出了工具环，本仓装配下框架回填没成立：{result.Thrown?.GetType().Name}: {result.Thrown?.Message}");
        Assert.Equal(2, inner.CallCount);
        Assert.True(inner.LastRequest.SelectMany(m => m.Contents).Any(c => c is FunctionResultContent),
            "异常没有被转成一条结果消息回到模型，回填机制在本仓装配下不成立");

        // 防恒真：同一驱动下让模型层自己抛异常，Thrown 必须非空 ——
        // 证明上面「不炸整轮」不是因为驱动永远吞掉异常才成立。
        var modelFailInner = new ScriptedChatClient()
            .ToolCall("ok")
            .Fail(new InvalidOperationException("模型层失败"));
        var modelFail = await PipelineDriver.DriveAsync(
            modelFailInner, okTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));
        Assert.NotNull(modelFail.Thrown);
    }

    // 工具结果可能被包成非 string；只取内容里 Result 的文本，避免"读不到就恒真"。
    private static string 结果文本(IReadOnlyList<ChatMessage> messages)
        => string.Concat(messages
            .SelectMany(m => m.Contents)
            .Select(c => c.GetType().GetProperty("Result")?.GetValue(c)?.ToString() ?? string.Empty));
}
