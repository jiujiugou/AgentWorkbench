using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Tools;

// 阶段 2 的核心判据：审批闸门必须落在「工具真正执行之前」，且失败一律倒向「不放行」。
//
// 表格五行（Auto / Deny / Ask+批准 / Ask+拒绝 / Ask+无应答）逐行正负控成对：
// 每一条 Assert.Equal(0, tool.Calls) 的负控就是它自己 —— 闸门一旦失效、工具被执行，
// 探针计数立刻变 1，用例当场变红。探针用 FaultInjectorTool（failTimes=0 时恒定成功并计数），
// 所以「没执行」与「执行了但恰好没留痕」不可能混为一谈。
public sealed class ToolApprovalGateTests
{
    [Fact]
    public async Task Auto_ExecutesTool_AndLeavesCleanTrace()
    {
        var c = await RunAsync(ToolApprovalMode.Auto);

        Assert.Equal(1, c.Tool.Calls);
        var step = Assert.Single(c.Run.Traces);
        Assert.Equal(FaultInjectorTool.ToolName, step.Name);
        Assert.Equal("ok", step.Result);
        Assert.Null(step.Error);            // Auto 不落审批归类码
        Assert.Equal("done", c.Response.Text);
        Assert.Equal(2, c.Client.Calls);    // 工具 → 回填 → 收口
    }

    [Fact]
    public async Task Deny_BlocksTool_AndRecordsPolicyCode()
    {
        var c = await RunAsync(ToolApprovalMode.Deny);

        // 负控即本行：闸门失效 → 计数变 1 → 红。
        Assert.Equal(0, c.Tool.Calls);

        var step = Assert.Single(c.Run.Traces);
        Assert.Equal(ToolApprovalCodes.PolicyDenied, step.Error);
        Assert.Contains("\"denied\":\"true\"", step.Result!);              // 拒绝载荷回填给了模型
        Assert.Contains(ToolApprovalCodes.PolicyDenied, step.Result!);

        // 被拒不阻断整轮：模型看到「被拒」后照常收口，轨迹仍是成功收尾。
        Assert.Equal("done", c.Response.Text);
        Assert.Null(c.Run.Stop);
    }

    [Fact]
    public async Task Ask_Approved_ExecutesTool()
    {
        var approver = new StubApprover(approve: true);

        var c = await RunAsync(ToolApprovalMode.Ask, approver);

        Assert.Equal(1, approver.Requests);                          // 确实问了人
        Assert.Equal(FaultInjectorTool.ToolName, approver.LastTool);
        Assert.Equal(1, c.Tool.Calls);                               // 批准 → 才执行
        var step = Assert.Single(c.Run.Traces);
        Assert.Equal("ok", step.Result);
        Assert.Null(step.Error);
    }

    [Fact]
    public async Task Ask_Denied_BlocksTool()
    {
        var approver = new StubApprover(approve: false);

        var c = await RunAsync(ToolApprovalMode.Ask, approver);

        Assert.Equal(1, approver.Requests);
        Assert.Equal(0, c.Tool.Calls);                               // 负控：若执行 → 1 → 红
        var step = Assert.Single(c.Run.Traces);
        Assert.Equal(ToolApprovalCodes.UserDenied, step.Error);
    }

    [Fact]
    public async Task Ask_WithoutApprover_FailsClosed()
    {
        // 没人可问就不放行 —— 这是 fail-closed 的语义边界，不是「没配就等于允许」。
        var c = await RunAsync(ToolApprovalMode.Ask, approver: null);

        Assert.Equal(0, c.Tool.Calls);
        var step = Assert.Single(c.Run.Traces);
        Assert.Equal(ToolApprovalCodes.Unavailable, step.Error);
    }

    [Fact]
    public async Task Ask_NoAnswer_TimesOut_FailsClosed()
    {
        // 问了但没人理：闸门必须靠自己的超时兜底，且结论是「拒绝」而不是「默认同意」。
        var silent = new SilentApprover();

        var c = await RunAsync(ToolApprovalMode.Ask, silent, timeoutSeconds: 1);

        Assert.Equal(0, c.Tool.Calls);                               // 负控：超时绝不能被当成批准
        var step = Assert.Single(c.Run.Traces);
        Assert.Equal(ToolApprovalCodes.Unavailable, step.Error);
    }

    [Fact]
    public async Task Auto_NeverConsultsApprover()
    {
        // Auto 不该触碰审批通道：既不打扰用户，也不把「通道故障」这个新失败面引进来。
        var approver = new StubApprover(approve: true);

        var c = await RunAsync(ToolApprovalMode.Auto, approver);

        Assert.Equal(0, approver.Requests);
        Assert.Equal(1, c.Tool.Calls);
    }

    [Fact]
    public async Task Denial_IsNotClassifiedAsToolFailure()
    {
        // 被拒 ≠ 工具失败：没有异常冒出环，归类层不得把它算成 tool_error。
        var c = await RunAsync(ToolApprovalMode.Deny);

        Assert.Empty(c.Run.ToolFailures);
        Assert.Null(c.Run.Stop);
    }

    // ── 夹具 ─────────────────────────────────────────────────────────────

    private static async Task<Case> RunAsync(
        ToolApprovalMode mode, IToolApprover? approver = null, int timeoutSeconds = 120)
    {
        var client = new ScriptedChatClient(call => call == 1
            ? ScriptedResponses.ToolCall(
                FaultInjectorTool.ToolName,
                ScriptedResponses.Args(("mode", "throw"), ("failTimes", 0), ("delayMs", 0)))
            : ScriptedResponses.Text("done"));

        var tool = new FaultInjectorTool();
        // 用真实的 ToolApprovalPolicy + 可变 monitor：审批级别走的就是生产那条配置链路。
        var policy = new ToolApprovalPolicy(new MutableOptionsMonitor<ToolApprovalOptions>(
            new ToolApprovalOptions
            {
                Default = ToolApprovalMode.Auto,
                Overrides = { [FaultInjectorTool.ToolName] = mode }
            }));

        var run = new ToolExecutionPipeline(
                Options.Create(new ToolExecutionOptions { ApprovalTimeoutSeconds = timeoutSeconds }),
                policy,
                NullLoggerFactory.Instance,
                new ServiceCollection().BuildServiceProvider())
            .Create(client, new ToolExecutionLimits(MaxIterations: 3, Approver: approver));

        var response = await run.Client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "按脚本调一次工具")],
            new ChatOptions { Tools = [.. tool.GetTools()] });

        return new Case(run, response, tool, client);
    }

    private sealed record Case(
        ToolExecutionRun Run, ChatResponse Response, FaultInjectorTool Tool, ScriptedChatClient Client);

    /// <summary>固定应答的审批者：记录被问了几次、问的是哪个工具。</summary>
    private sealed class StubApprover(bool approve) : IToolApprover
    {
        public int Requests { get; private set; }

        public string? LastTool { get; private set; }

        public Task<bool> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests++;
            LastTool = request.ToolName;
            return Task.FromResult(approve);
        }
    }

    /// <summary>永不回答的审批者：模拟「问了但没人理」——闸门必须靠自己的超时兜底。</summary>
    private sealed class SilentApprover : IToolApprover
    {
        public Task<bool> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
            => new TaskCompletionSource<bool>().Task;
    }
}
