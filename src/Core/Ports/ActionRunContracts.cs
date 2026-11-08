namespace AgentWorkbench.Ports;

/// <summary>
/// 一次动作 run 的状态取值（M1 设计 §2.4）。这里只定义状态字符串，
/// 状态迁移规则在底座侧实现；Rejected / PendingApproval / Unknown 三个状态不得产生外部副作用。
/// </summary>
public static class ActionRunStates
{
    public const string Received = "received";
    public const string Intent = "intent";
    public const string Validated = "validated";
    public const string PendingApproval = "pending_approval";
    public const string Approved = "approved";
    public const string Executing = "executing";
    public const string Unknown = "unknown";
    public const string Completed = "completed";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
}

/// <summary>
/// Checkpoint 只存框架内部执行进度字节，不存业务状态（ADR 0017 §四）。
/// ProgressBlob 对底座同样不透明；业务单据状态属于业务库。
/// </summary>
public sealed record RunCheckpoint(
    string RequestId,
    string State,
    string ProgressBlob,
    DateTimeOffset At);

/// <summary>审批结论。Approver 为空时不得伪造；审批人 / 层级 / 时长由动作域注入的策略决定。</summary>
public sealed record ApprovalDecision(bool Approved, string? Approver, string Reason);

/// <summary>审批闸：注入口，底座只拿 approved / approver，不理解审批规则内容。</summary>
public interface IApprovalGate
{
    ValueTask<ApprovalDecision> RequestAsync(ActionRequest request, CancellationToken ct);
}

/// <summary>
/// run 状态与幂等存储（底座侧）。Suspend / Resume 只处理执行进度；
/// TryClaimIdempotencyAsync 保证同一 request_id 只领取一次副作用，去重表在底座不在动作域。
/// </summary>
public interface IActionRunStore
{
    ValueTask<RunCheckpoint?> LoadCheckpointAsync(string requestId, CancellationToken ct);

    /// <summary>按状态列待办（宿主审批页用）。只返回 Checkpoint，不含业务单据内容。</summary>
    ValueTask<IReadOnlyList<RunCheckpoint>> ListCheckpointsAsync(string state, CancellationToken ct);

    ValueTask<RunCheckpoint> SuspendAsync(string requestId, string state, string progressBlob, CancellationToken ct);
    ValueTask<RunCheckpoint?> ResumeAsync(string requestId, CancellationToken ct);
    ValueTask<bool> TryClaimIdempotencyAsync(string requestId, string? idempotencyKey, CancellationToken ct);
    ValueTask<ActionResult?> LoadResultAsync(string requestId, CancellationToken ct);
    ValueTask SaveResultAsync(ActionResult result, CancellationToken ct);
}

/// <summary>审计反查：按 request_id 取回完整轨迹，供「谁 / 何时 / 看到什么 / 依据哪条规则 / 变成什么」复核。</summary>
public interface IAuditQuery
{
    ValueTask<IReadOnlyList<TraceEvent>> AuditTrailAsync(string requestId, CancellationToken ct);
}

/// <summary>
/// 动作集组合契约：组合根只替换这一个实例即可换域（AC-01）。
/// 业务语义全部由动作域提供，Core 下任何项目不得引用 Domains。
/// </summary>
public interface IActionSet
{
    string Name { get; }
    IActionSpecRegistry Specs { get; }
    ISchemaValidator Schema { get; }
    IPermissionQuery Permissions { get; }
    IBusinessRule Rules { get; }
    IActionExecutor Executor { get; }
    IApprovalGate Approvals { get; }
    // 超时后只用它发起查证，绝不重放写动作（M1 设计 §2.2 步骤 5 / §5.2）。
    ActionSpec QuerySpec { get; }
}
