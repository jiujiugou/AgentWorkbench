namespace AgentWorkbench.ToolExecution;

/// <summary>一次审批请求：把「哪个工具、什么参数」交给审批执行者去问人。</summary>
/// <param name="ToolName">待审批的工具名。</param>
/// <param name="Arguments">已序列化、键序稳定的参数文本；无参数为 <c>null</c>。</param>
public sealed record ToolApprovalRequest(string ToolName, string? Arguments);

/// <summary>
/// 审批执行者（<b>run 级</b>）：把「要人工确认」这件事落到具体通道（如前端对话框往返）。
/// </summary>
/// <remarks>
/// <para>
/// 契约是 <b>fail-closed</b>：只有明确得到批准才允许返回 <c>true</c>；
/// 超时、连接断开、无人应答、通道自身故障一律返回 <c>false</c>（等于拒绝）。
/// </para>
/// <para>
/// 实现方不必自己保证不会无限等待 —— 管道侧另有超时兜底
/// （<see cref="ToolExecutionOptions.ApprovalTimeoutSeconds"/>）。两侧都往「不放行」倒，
/// 任何一侧失灵都不会退化成「没批准也执行」。
/// </para>
/// <para>
/// 管道只依赖这个接口，不依赖任何具体通道：把 SignalR / 控制台 / 测试替身换进来都一样。
/// </para>
/// </remarks>
public interface IToolApprover
{
    /// <summary>请求人工确认；<c>true</c> = 批准执行，<c>false</c> = 拒绝。</summary>
    Task<bool> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 审批被拒的归类码：写进工具轨迹的 <see cref="ToolCallTrace.Error"/>，
/// 供回放与判分区分「被拒」与「工具执行失败」——两者在结果文本上都可能是空或一段说明。
/// </summary>
public static class ToolApprovalCodes
{
    /// <summary>策略直接拒绝（<see cref="AgentWorkbench.Safety.ToolApprovalMode.Deny"/>）。</summary>
    public const string PolicyDenied = "approval_policy_denied";

    /// <summary>人工明确拒绝（<see cref="AgentWorkbench.Safety.ToolApprovalMode.Ask"/> 且应答为否）。</summary>
    public const string UserDenied = "approval_user_denied";

    /// <summary>无法取得批准：超时 / 未提供审批者 / 审批通道故障 —— 一律 fail-closed。</summary>
    public const string Unavailable = "approval_unavailable";
}
