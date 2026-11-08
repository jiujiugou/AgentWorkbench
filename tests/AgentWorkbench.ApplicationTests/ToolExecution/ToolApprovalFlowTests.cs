using AgentWorkbench.ApplicationTests.Support;
using AgentWorkbench.Runtime.Tools;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ToolExecution;

/// <summary>
/// F2.3 fail-closed / F2.4 拒绝回填 / F2.6 轨迹 / F2.7 异常归类。
/// 全部通过真实工具环（run.Client）驱动，断言落在执行次数、请求次数、轨迹条数与 error 具体字符串上。
/// </summary>
public class ToolApprovalFlowTests
{
    // ===================== F2.3 fail-closed =====================

    // [F2.3] Ask 但没有审批者：无人可问即拒绝，工具执行 0 次，轨迹 error=approval_unavailable。
    // 负控：把 ToolExecutionPipeline.cs:226-227 的
    //       `if (limits.Approver is null) return ToolApprovalCodes.Unavailable;` 改成 `return null;`（fail-open），
    //       工具执行次数从 0 变 1、error 变 null，下面断言变红。
    [Fact]
    public async Task Ask_无审批者_拒绝_工具零次且_error_为_unavailable()
    {
        var tool = CountingTool.NoArg("ask_tool", () => "不该被执行");
        var inner = new ScriptedChatClient().ToolCall("ask_tool").Reply("换路");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Ask), approver: null);

        Assert.Equal(0, tool.Executions);
        Assert.Equal(1, result.Run.Traces.Count);
        Assert.Equal(ToolApprovalCodes.Unavailable, result.Run.Traces[0].Error);
        Assert.Contains("denied", result.Run.Traces[0].Result!); // 拒绝也作为结构化载荷回填
    }

    // [F2.3] Ask 审批等待超时：等不到答复不等于同意，工具执行 0 次，error=approval_unavailable。
    // 负控：把 ToolExecutionPipeline.cs:237 的 .WaitAsync(timeout, token) 去掉（无限等），
    //       或把 catch(TimeoutException) 分支（:240-244）改成放行，本条会挂住或变红。
    [Fact]
    public async Task Ask_审批超时_拒绝_工具零次且_error_为_unavailable()
    {
        var tool = CountingTool.NoArg("ask_tool", () => "不该被执行");
        var hanging = new StubApprover((_, _) => new TaskCompletionSource<bool>().Task);
        var inner = new ScriptedChatClient().ToolCall("ask_tool").Reply("换路");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Ask),
            approver: hanging, approvalTimeoutSeconds: 1);

        Assert.Equal(1, hanging.Calls);
        Assert.Equal(0, tool.Executions);
        Assert.Equal(1, result.Run.Traces.Count);
        Assert.Equal(ToolApprovalCodes.Unavailable, result.Run.Traces[0].Error);
    }

    // [F2.3] Ask 审批通道自身故障（RequestAsync 抛异常）：一律 fail-closed，工具执行 0 次。
    // 负控：删掉 ToolExecutionPipeline.cs:250-254 的 catch (Exception) { return Unavailable; }，
    //       异常冒泡、不再产生拒绝轨迹，下面断言变红。
    [Fact]
    public async Task Ask_审批通道故障_拒绝_工具零次且_error_为_unavailable()
    {
        var tool = CountingTool.NoArg("ask_tool", () => "不该被执行");
        var broken = new StubApprover((_, _) => Task.FromException<bool>(new IOException("审批通道断开")));
        var inner = new ScriptedChatClient().ToolCall("ask_tool").Reply("换路");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Ask), approver: broken);

        Assert.Equal(1, broken.Calls);
        Assert.Equal(0, tool.Executions);
        Assert.Equal(1, result.Run.Traces.Count);
        Assert.Equal(ToolApprovalCodes.Unavailable, result.Run.Traces[0].Error);
    }

    // [F2.3] 反例对照（该放行的）：Ask 只要拿到明确批准就执行，不被上面三条 fail-closed 误伤。
    // 负控：把 ToolExecutionPipeline.cs:238 改成恒返回 UserDenied，工具执行次数从 1 变 0，本条变红。
    [Fact]
    public async Task Ask_明确批准则放行_与三条拒绝场景形成对照()
    {
        var tool = CountingTool.NoArg("ask_tool", () => "OK");
        var approver = new StubApprover(true);
        var inner = new ScriptedChatClient().ToolCall("ask_tool").Reply("完成");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Ask), approver: approver);

        Assert.Equal(1, tool.Executions);
        Assert.Equal(1, approver.Calls);
        Assert.Null(result.Run.Traces[0].Error);
        Assert.Equal("OK", result.Run.Traces[0].Result);
    }

    // ===================== F2.4 拒绝回填 =====================

    // [F2.4] Deny  PolicyDenied：被拒作为结果回填模型，模型发生第二次请求，且不重试同一调用。
    // 负控：把 ToolExecutionPipeline.cs:165-173 的拒绝分支改成「静默收口」（context.Terminate=true 且不回填），
    //       inner.CallCount 从 2 变 1、模型第 2 次请求里不再含 denied 载荷，下面断言变红。
    [Fact]
    public async Task Deny_拒绝回填_模型收到载荷且发生第二次请求()
    {
        var tool = CountingTool.NoArg("secure_tool", () => "不该被执行");
        var inner = new ScriptedChatClient().ToolCall("secure_tool").Reply("改用其它方式");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Deny));

        Assert.Equal(0, tool.Executions);
        Assert.Equal(2, inner.CallCount);          // 被拒后仍回问模型一次
        Assert.Equal(1, result.Run.Traces.Count);  // 不重试同一调用
        Assert.Equal(ToolApprovalCodes.PolicyDenied, result.Run.Traces[0].Error);
        Assert.Contains("denied", result.Run.Traces[0].Result!);
        Assert.Contains("approval_policy_denied", result.Run.Traces[0].Result!);
        // 模型第 2 次请求里确实收到了结构化拒绝载荷
        Assert.Contains("denied", RequestInspector.Dump(inner.Requests[1]));
    }

    // [F2.4] Ask + 人工拒绝（UserDenied）：同样回填、同样发生第二次请求，error 精确为 approval_user_denied。
    // 负控：把 ToolExecutionPipeline.cs:238 的 `approved ? null : UserDenied` 改成恒返回 null，
    //       工具执行次数从 0 变 1、error 变 null，下面断言变红。
    [Fact]
    public async Task Ask_用户拒绝_回填载荷且_error_为_user_denied()
    {
        var tool = CountingTool.NoArg("ask_tool", () => "不该被执行");
        var approver = new StubApprover(false);
        var inner = new ScriptedChatClient().ToolCall("ask_tool").Reply("换路");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Ask), approver: approver);

        Assert.Equal(0, tool.Executions);
        Assert.Equal(1, approver.Calls);
        Assert.Equal(2, inner.CallCount);
        Assert.Equal(ToolApprovalCodes.UserDenied, result.Run.Traces[0].Error);
        Assert.Contains("approval_user_denied", result.Run.Traces[0].Result!);
    }

    // [F2.4] 反例对照（该放行的）：Auto 时回填的是工具真实结果，绝不含拒绝载荷。
    // 负控：把 Auto 分支也走 DenialPayload，下面 Result / DoesNotContain 两条断言变红。
    [Fact]
    public async Task Auto_回填真实结果_不含拒绝载荷_与拒绝场景形成对照()
    {
        var tool = CountingTool.NoArg("safe_tool", () => "真实结果");
        var inner = new ScriptedChatClient().ToolCall("safe_tool").Reply("完成");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal(1, tool.Executions);
        Assert.Equal(2, inner.CallCount);
        Assert.Null(result.Run.Traces[0].Error);
        Assert.Equal("真实结果", result.Run.Traces[0].Result);
        Assert.DoesNotContain("denied", result.Run.Traces[0].Result!);
    }

    // ===================== F2.6 轨迹 =====================

    // [F2.6] 一次调用记全 名/参数原文/结果/错误码/轮次，轨迹条数 = 调用次数。
    // 负控：把 ToolExecutionPipeline.cs:145-146 的 Serialize(context.Arguments) 改成 null 或固定串，
    //       Arguments 含 "1+2" 的断言变红；删掉 BeginCall 则 Traces.Count 从 1 变 0。
    [Fact]
    public async Task 轨迹字段齐全_且条数等于调用次数()
    {
        var tool = CountingTool.OneArg("calc", input => "结果:" + input);
        var arguments = new Dictionary<string, object?> { ["input"] = "1+2" };
        var inner = new ScriptedChatClient().ToolCall("calc", arguments).Reply("完成");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal(1, tool.Executions);
        Assert.Equal(1, result.Run.Traces.Count); // 条数 = 调用次数
        var trace = result.Run.Traces[0];
        Assert.Equal("calc", trace.Name);          // 记名
        // 参数原文是 JSON；`+` 被序列化成 \u002B，不能直接找字面量 "1+2"
        using var args = System.Text.Json.JsonDocument.Parse(trace.Arguments!);
        Assert.Equal("1+2", args.RootElement.GetProperty("input").GetString());
        Assert.Equal("结果:1+2", trace.Result);    // 结果
        Assert.Null(trace.Error);
        Assert.Equal(0, trace.Iteration);          // 轮次（0 起）
    }

    // [F2.6] 拒绝与工具失败在 error 上可区分：成功 null / 拒绝 policy_denied / 失败含异常类型与消息，两两不等。
    // 负控：把拒绝分支的 error:denial 或异常分支的 error:Truncate(...) 去掉（都记 null），
    //       三者 error 全为 null，NotEqual / Contains 断言变红。
    [Fact]
    public async Task 拒绝与失败在_error_上可区分()
    {
        var okTool = CountingTool.NoArg("ok", () => "OK");
        var ok = await PipelineDriver.DriveAsync(
            new ScriptedChatClient().ToolCall("ok").Reply("完成"),
            okTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        var denyTool = CountingTool.NoArg("deny", () => "x");
        var deny = await PipelineDriver.DriveAsync(
            new ScriptedChatClient().ToolCall("deny").Reply("换路"),
            denyTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Deny));

        var failTool = CountingTool.NoArg("boom", () => throw new InvalidOperationException("boom-msg"));
        var fail = await PipelineDriver.DriveAsync(
            new ScriptedChatClient().ToolCall("boom").Reply("不应到达"),
            failTool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        var okError = ok.Run.Traces[0].Error;
        var denyError = deny.Run.Traces[0].Error;
        var failError = fail.Run.Traces[0].Error;

        Assert.Null(okError);
        Assert.Equal(ToolApprovalCodes.PolicyDenied, denyError);
        Assert.Contains("InvalidOperationException", failError!);
        Assert.Contains("boom-msg", failError!);
        Assert.NotEqual(denyError, failError); // 拒绝 vs 失败：字符串不同
        Assert.NotNull(denyError);
    }

    // ===================== F2.7 异常归类 =====================

    // [F2.7] 工具异常：实例存进 ToolFailures（供 Harness 依 AgentRunCode.ToolError 归类），
    //        轨迹 Result=null、Error 含异常类型与消息，绝不静默吞成 succeeded。
    //        异常原样 throw 回 MEA；是否向外冒泡由 MEA 决定，故本条只判 pipeline 的留痕与「不吞」。
    // 负控：把 ToolExecutionPipeline.cs:193-199 的 catch 改成 `return null;`（吞掉），
    //       ToolFailures 从 1 变 0、Error 变 null，下面断言变红；
    //       把 error 改成固定串则「含类型与消息」两条断言变红。
    [Fact]
    public async Task 工具异常_记_ToolFailures_且轨迹含类型与消息()
    {
        var tool = CountingTool.NoArg("boom", () => throw new InvalidOperationException("boom-message"));
        var inner = new ScriptedChatClient().ToolCall("boom").Reply("不应到达");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal(1, result.Run.ToolFailures.Count);
        Assert.IsType<InvalidOperationException>(result.Run.ToolFailures[0]);
        Assert.Contains("boom-message", result.Run.ToolFailures[0].Message);
        Assert.Equal(1, result.Run.Traces.Count);
        Assert.Null(result.Run.Traces[0].Result);
        Assert.Contains("InvalidOperationException", result.Run.Traces[0].Error!);
        Assert.Contains("boom-message", result.Run.Traces[0].Error!);
    }

    // [F2.7] 反例对照（该放行的）：正常工具不产生 ToolFailures、不抛、轨迹 error=null、Result 为真实结果。
    // 负控：把 CompleteCall 在正常路径也写成 error，下面 Assert.Null(Error) 变红。
    [Fact]
    public async Task 正常工具_不产生_ToolFailures_也不记_error()
    {
        var tool = CountingTool.NoArg("ok", () => "OK");
        var inner = new ScriptedChatClient().ToolCall("ok").Reply("完成");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Null(result.Thrown);
        Assert.Equal(0, result.Run.ToolFailures.Count);
        Assert.Equal(1, result.Run.Traces.Count);
        Assert.Null(result.Run.Traces[0].Error);
        Assert.Equal("OK", result.Run.Traces[0].Result);
    }
}
