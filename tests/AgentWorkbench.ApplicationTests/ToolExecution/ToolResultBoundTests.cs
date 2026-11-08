// 功能未实现，本组预期红，红是正确信号。
// F2.9「进入模型的结果有上界」当前不存在：MaxResultChars=400 只截轨迹
// （ToolExecutionPipeline.cs 把 Truncate(text) 存进轨迹），但仍 `return result;` —— 模型拿到完整结果。
//
// 红目标改用**源码结构断言**，原因：MEA 把工具结果包成 FunctionResultContent，
// Result 可能是 JsonElement 而非 string；用反射读 string 会得到长度 0，
// 于是「长度 ≤ 上界」恒成立 —— 那是假绿，正是 11-01 要防的形态。
// 结构断言能红能绿：把 `return result;` 改成 `return Truncate(text);` 后本组转绿。
// 每条用例先给绿对照（轨迹确实被截断），再给红目标。

using AgentWorkbench.ApplicationTests.Support;
using AgentWorkbench.Runtime.Tools;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ToolExecution;

public class ToolResultBoundTests
{
    // [F2.9] 10 万字符结果：轨迹被截断（绿对照），回填给模型的也不得是完整结果（红目标）。
    [Fact]
    public async Task 超大结果_进入模型的内容应有上界_小结果放行作对照()
    {
        // 绿对照：小结果原样进模型
        var smallTool = CountingTool.NoArg("small", () => "OK");
        var smallInner = new ScriptedChatClient().ToolCall("small").Reply("完成");
        var small = await PipelineDriver.DriveAsync(
            smallInner, smallTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal(1, smallTool.Executions);
        Assert.Equal("OK", small.Run.Traces[0].Result);

        // 绿对照：10 万字符结果，轨迹确实按 MaxResultChars 截断
        var bigTool = CountingTool.NoArg("big", () => new string('x', 100_000));
        var bigInner = new ScriptedChatClient().ToolCall("big").Reply("完成");
        var big = await PipelineDriver.DriveAsync(
            bigInner, bigTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal(1, bigTool.Executions);
        Assert.Equal(400, big.Run.Traces[0].Result!.Length);

        // 红目标：回填给模型的值不得是完整结果
        var code = ReadPipelineSource();
        Assert.Contains("class ToolExecutionPipeline", code); // 扫描器自证：确实读到目标文件
        Assert.DoesNotContain("return result;", code);
        // 负控 [F2.9]：把 `return result;` 改成 `return Truncate(text);` → 本条转绿；当前恒红。
    }

    // [F2.9] 上界须随 MaxResultChars 变化：调成 50 后，回填值也应收敛到 50。
    [Fact]
    public async Task 上界随_MaxResultChars_变化_模型结果也应随之收敛()
    {
        var tool = CountingTool.NoArg("big", () => new string('x', 100_000));
        var inner = new ScriptedChatClient().ToolCall("big").Reply("完成");
        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto), maxResultChars: 50);

        Assert.Equal(50, result.Run.Traces[0].Result!.Length); // 绿对照：轨迹按 50 截断

        var code = ReadPipelineSource();
        Assert.Contains("Truncate(", code); // 扫描器自证
        Assert.Contains("return Truncate(text);", code);
        // 负控 [F2.9]：同上；当前没有显式返回截断值 → 本条恒红。
    }

    private static string ReadPipelineSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentWorkbench.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName,
            "src/Core/Runtime/AgentWorkbench.Runtime.Tools/ToolExecutionPipeline.cs"));
    }
}
