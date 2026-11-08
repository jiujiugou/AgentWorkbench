using AgentWorkbench.ApplicationTests.Support;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Shared;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ToolExecution;

/// <summary>
/// F2.5 结果否决：工具已返回、回填模型之前可终止本轮。
/// 原文件随 tests/ 重建被删除，这里按验收文档补回 3 条。
/// </summary>
public class ToolResultVetoTests
{
    private static IToolResultVeto Veto(AgentRunStop? stop)
    {
        var mock = new Mock<IToolResultVeto>();
        mock.Setup(v => v.Inspect(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(stop);
        return mock.Object;
    }

    private static AgentRunStop Insufficient()
        => new(AgentRunStatus.Refused, AgentRunCode.KnowledgeInsufficient, "依据不足，拒绝回答");

    // [F2.5] 判不足时模型只被请求 1 次，且 run.Stop.status=refused / code=knowledge_insufficient。
    // 负控：删掉 ToolExecutionPipeline.cs:184-189 的 veto 判定（context.Terminate=true 与 StopAt），
    //       模型会被回填后再问一次（CallCount 从 1 变 2）、Stop 变 null，下面断言变红。
    [Fact]
    public async Task 判不足时_模型只被请求一次且_status_为_refused()
    {
        var tool = CountingTool.NoArg("search_knowledge", () => "少量命中");
        var inner = new ScriptedChatClient().ToolCall("search_knowledge").Reply("本不该出现的答复");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto), veto: Veto(Insufficient()));

        Assert.Equal(1, inner.CallCount);
        Assert.Equal(1, tool.Executions);
        Assert.NotNull(result.Run.Stop);
        Assert.Equal(AgentRunStatus.Refused, result.Run.Stop!.Status);
        Assert.Equal(AgentRunCode.KnowledgeInsufficient, result.Run.Stop.Code);
    }

    // [F2.5] 放行（Veto 返回 null）时模型被请求 2 次（工具结果回填后再答一次），Stop 为 null。
    // 这是上一条「判不足」的反例对照（该放行的）。
    // 负控：把 pipeline 的 veto 判定改成无条件 Terminate，CallCount 从 2 变 1，本条变红。
    [Fact]
    public async Task 放行时_模型被请求两次()
    {
        var tool = CountingTool.NoArg("search_knowledge", () => "命中内容");
        var inner = new ScriptedChatClient().ToolCall("search_knowledge").Reply("最终答复");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto), veto: Veto(null));

        Assert.Equal(2, inner.CallCount);
        Assert.Equal(1, tool.Executions);
        Assert.Null(result.Run.Stop);
        Assert.Equal("命中内容", result.Run.Traces[0].Result);
    }

    // [F2.5] 拒答时轨迹仍保留工具结果（拒答也要能反查「它看到了什么才拒的」）。
    // 负控：把 ToolExecutionPipeline.cs:180 的 CompleteCall 挪到 veto 判定之后，或 veto 时清空轨迹，
    //       Result 会变 null / 轨迹条数变 0，下面断言变红。
    [Fact]
    public async Task 拒答时_轨迹仍保留工具结果()
    {
        var tool = CountingTool.NoArg("search_knowledge", () => "命中片段内容");
        var inner = new ScriptedChatClient().ToolCall("search_knowledge").Reply("不应到达");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto), veto: Veto(Insufficient()));

        Assert.Equal(1, result.Run.Traces.Count);
        Assert.Equal("search_knowledge", result.Run.Traces[0].Name);
        Assert.Equal("命中片段内容", result.Run.Traces[0].Result);
        Assert.Null(result.Run.Traces[0].Error);
        Assert.Equal(1, inner.CallCount);
    }
}
