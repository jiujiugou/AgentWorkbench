using AgentWorkbench.ApplicationTests.Support;
using AgentWorkbench.Runtime.Tools;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ToolExecution;

/// <summary>
/// F2.2 审批三态：Auto 直接执行 / Ask 问人 / Deny 拒绝。
/// 前半测「策略本体」Decision 的具体枚举值，后半测「三种策略各跑一次」的工具执行次数。
/// </summary>
public class ToolApprovalPolicyTests
{
    private static ToolApprovalPolicy PolicyWith(ToolApprovalOptions options)
    {
        var monitor = new Mock<IOptionsMonitor<ToolApprovalOptions>>();
        monitor.SetupGet(m => m.CurrentValue).Returns(options);
        return new ToolApprovalPolicy(monitor.Object);
    }

    // [F2.2] Decision 对三种策略各返回具体枚举值（Auto/Ask/Deny），不是「非 Deny 就放行」这种模糊判断。
    // 负控：把 ToolApprovalPolicy.cs:47 的
    //       `options.Overrides.TryGetValue(toolName, out var mode) ? mode : options.Default`
    //       改成恒返回 ToolApprovalMode.Auto，deny_tool / ask_tool 两条具体值断言变红。
    [Fact]
    public void Decision_三态各返回具体枚举值()
    {
        var policy = PolicyWith(new ToolApprovalOptions
        {
            Default = ToolApprovalMode.Auto,
            Overrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["ask_tool"] = ToolApprovalMode.Ask,
                ["deny_tool"] = ToolApprovalMode.Deny
            }
        });

        Assert.Equal(ToolApprovalMode.Auto, policy.Decision("plain_tool")); // 该放行
        Assert.Equal(ToolApprovalMode.Ask, policy.Decision("ask_tool"));
        Assert.Equal(ToolApprovalMode.Deny, policy.Decision("deny_tool"));  // 该拒绝
    }

    // [F2.2] Overrides 优先于 Default：默认拒绝（该拒的）时，显式列出的工具仍放行（该放行的）。
    // 负控：删掉 TryGetValue 分支、恒返回 options.Default，auto_tool 那条断言变红。
    [Fact]
    public void Overrides_覆盖_Default_默认拒绝时显式工具仍放行()
    {
        var policy = PolicyWith(new ToolApprovalOptions
        {
            Default = ToolApprovalMode.Deny,
            Overrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["auto_tool"] = ToolApprovalMode.Auto
            }
        });

        Assert.Equal(ToolApprovalMode.Auto, policy.Decision("auto_tool")); // 该放行
        Assert.Equal(ToolApprovalMode.Deny, policy.Decision("other_tool")); // 该拒绝
    }

    // [F2.2] AutoToolApprovalPolicy 对任意工具恒为 Auto（「这一处不做审批」是明确取值，不是 null）。
    // 负控：把 ToolApprovalPolicy.cs:68 的返回值改成 Ask 或 Deny，下面 3 条具体值断言全红。
    [Fact]
    public void AutoToolApprovalPolicy_对任意工具恒为_Auto()
    {
        var policy = AutoToolApprovalPolicy.Instance;

        Assert.Equal(ToolApprovalMode.Auto, policy.Decision("a"));
        Assert.Equal(ToolApprovalMode.Auto, policy.Decision("deny_me"));
        Assert.Equal(ToolApprovalMode.Auto, policy.Decision("anything"));
    }

    // [F2.2] Auto 直接执行：工具被调用 1 次，轨迹 1 条、无 error、结果是工具真实输出。
    // 负控：把 InspectApprovalAsync 的 Auto 分支（ToolExecutionPipeline.cs:219-220）改成继续走审批，
    //       工具执行次数会从 1 变 0（无审批者被 fail-closed 拦下），下面断言变红。
    [Fact]
    public async Task Auto_直接执行_工具被调用一次()
    {
        var tool = CountingTool.NoArg("auto_tool", () => "OK");
        var inner = new ScriptedChatClient().ToolCall("auto_tool").Reply("完成");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Auto));

        Assert.Equal(1, tool.Executions);
        Assert.Equal(1, result.Run.Traces.Count);
        Assert.Null(result.Run.Traces[0].Error);
        Assert.Equal("OK", result.Run.Traces[0].Result);
        Assert.Equal(2, inner.CallCount); // 结果回填后模型再答一次
    }

    // [F2.2] Deny 拒绝执行：工具 0 次，轨迹 error 精确为 approval_policy_denied。
    // 负控：把 ToolExecutionPipeline.cs:222-223 的
    //       `if (mode == Deny) return ToolApprovalCodes.PolicyDenied;` 改成 `return null;`（Deny 改 Auto），
    //       工具执行次数从 0 变 1、error 变 null，两条断言一起变红。
    [Fact]
    public async Task Deny_拒绝执行_工具零次且轨迹记_policy_denied()
    {
        var tool = CountingTool.NoArg("deny_tool", () => "不该被执行");
        var inner = new ScriptedChatClient().ToolCall("deny_tool").Reply("换路");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Deny));

        Assert.Equal(0, tool.Executions);
        Assert.Equal(1, result.Run.Traces.Count);
        Assert.Equal(ToolApprovalCodes.PolicyDenied, result.Run.Traces[0].Error);
    }

    // [F2.2] Ask 且明确批准：审批者被问 1 次，工具执行 1 次，轨迹无 error。
    // 这是 Deny（该拒绝）的反例对照（该放行）。
    // 负控：把 ToolExecutionPipeline.cs:238 的 `approved ? null : UserDenied` 改成恒返回 UserDenied，
    //       工具执行次数从 1 变 0，本条变红。
    [Fact]
    public async Task Ask_明确批准后执行_工具被调用一次()
    {
        var tool = CountingTool.NoArg("ask_tool", () => "OK");
        var approver = new StubApprover(true);
        var inner = new ScriptedChatClient().ToolCall("ask_tool").Reply("完成");

        var result = await PipelineDriver.DriveAsync(
            inner, tool.Tool, new FixedApprovalPolicy(ToolApprovalMode.Ask), approver: approver);

        Assert.Equal(1, tool.Executions);
        Assert.Equal(1, approver.Calls);
        Assert.Equal(1, result.Run.Traces.Count);
        Assert.Null(result.Run.Traces[0].Error);
    }
}
