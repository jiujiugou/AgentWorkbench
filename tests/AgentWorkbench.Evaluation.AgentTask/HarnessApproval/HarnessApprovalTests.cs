using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentLoop;
using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.Shared;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.Evaluation.AgentTask.HarnessApproval;

/// <summary>
/// 阶段 2 的端到端判据：审批闸门必须**真的接在 harness 这条路上**，
/// 而不是「代码写好了、没人调用」——后者正是这个仓库被反复诊断出的「没有消费者」病。
///
/// 本域断言的是「<see cref="HarnessRunRequest.Approver"/> 从调用方一路走到工具执行环」，
/// 以及在**账本**（回放与判分真正读的那份）里留下审批归类码。
/// </summary>
public sealed class HarnessApprovalTests
{
    private static AgentLoopOptions Defaults()
        => new() { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 3 };

    /// <summary>第 1 轮调工具、第 2 轮收口的健康脚本（工具本身恒定成功，是否执行由审批决定）。</summary>
    private static ChatResponse ToolCall()
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", "throw"), ("failTimes", 0), ("delayMs", 0)));

    private static HarnessUnderTest Build() => HarnessUnderTest.Create(
        call => call == 1 ? ToolCall() : ScriptedResponses.Text("done"),
        Defaults(),
        approval: new FixedToolApprovalPolicy(FaultInjectorTool.ToolName, ToolApprovalMode.Ask));

    [Fact]
    public async Task ApproverFromRequest_ReachesPipeline_AndApprovedToolExecutes()
    {
        var approver = new RecordingApprover(approve: true);
        var harness = Build();

        var result = await harness.Harness.RunAsync(
            new HarnessRunRequest(null, "问一次工具", Approver: approver));

        Assert.Equal(1, approver.Requests);                    // 请求里的 approver 真的到了管道
        Assert.Equal(FaultInjectorTool.ToolName, approver.LastTool);
        Assert.Equal(1, harness.Tool.Calls);                   // 批准 → 才执行

        var step = Assert.Single(result.Steps, s => s.Kind == "tool");
        Assert.Null(step.Error);                               // 放行 → 不落审批归类码
        Assert.Equal(AgentRunStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task ApproverFromRequest_WhenDenied_ToolNeverExecutes_AndLedgerRecordsCode()
    {
        var approver = new RecordingApprover(approve: false);
        var harness = Build();

        var result = await harness.Harness.RunAsync(
            new HarnessRunRequest(null, "问一次工具", Approver: approver));

        Assert.Equal(1, approver.Requests);
        Assert.Equal(0, harness.Tool.Calls);                   // 负控：闸门失效 → 1 → 红

        var step = Assert.Single(result.Steps, s => s.Kind == "tool");
        Assert.Equal(ToolApprovalCodes.UserDenied, step.Error);

        // 账本是回放与判分的唯一事实源：拒绝必须落进去，而不只在内存结果对象里。
        var trace = Assert.Single(harness.Traces.Saved);
        Assert.Contains(trace.Steps, s => s.Kind == "tool" && s.Error == ToolApprovalCodes.UserDenied);
        Assert.Equal(AgentRunStatus.Succeeded, trace.Status);  // 被拒不等于整轮失败
    }

    [Fact]
    public async Task AskWithoutApprover_FailsClosed_ThroughHarness()
    {
        // 请求里没带 approver：Ask 级工具一律不放行（fail-closed），绝不静默降级成「允许」。
        var harness = Build();

        var result = await harness.Harness.RunAsync(new HarnessRunRequest(null, "不带 approver"));

        Assert.Equal(0, harness.Tool.Calls);
        var step = Assert.Single(result.Steps, s => s.Kind == "tool");
        Assert.Equal(ToolApprovalCodes.Unavailable, step.Error);
    }

    /// <summary>把指定工具固定到某个审批级别的策略；其余工具一律 Auto。</summary>
    private sealed class FixedToolApprovalPolicy(string toolName, ToolApprovalMode mode) : IToolApprovalPolicy
    {
        public ToolApprovalMode Decision(string name)
            => string.Equals(name, toolName, StringComparison.Ordinal) ? mode : ToolApprovalMode.Auto;
    }

    /// <summary>固定应答的审批者：记录被问了几次、问的是哪个工具。</summary>
    private sealed class RecordingApprover(bool approve) : IToolApprover
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
}
