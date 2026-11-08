namespace AgentWorkbench.ActionGateway;

using AgentWorkbench.Ports;

/// <summary>
/// 一次动作 run 的编排（M1 设计 §2.2 六步 / §2.4 状态机 / §6 幂等与审计）。
/// 输入必须是结构化 ActionRequest —— 模型只负责把自然语言翻成它，本类不依赖模型，
/// 因此「拿掉模型系统仍能安全拒绝」在结构上成立，而不是靠提示词保证。
///
/// 两种审批形态：
/// - <see cref="RunAsync"/>：同步审批（IApprovalGate 当场给结论），用于测试与无人工场景；
/// - <see cref="SubmitDeferredAsync"/> + <see cref="ApproveAsync"/> / <see cref="RejectAsync"/>：
///   两阶段挂起，供宿主「待办 + 同意/拒绝」最小闭环使用。挂起期间零外部调用。
///
/// 共同顺序铁律：四道校验 → 审批 → 幂等领取 → 执行 → 超时只查证 → 收口。
/// </summary>
public sealed class ActionRunService
{
    private readonly IActionSet _actionSet;
    private readonly FailClosedActionGateway _gateway;
    private readonly ITraceSink _trace;
    private readonly IActionRunStore _store;
    private readonly IAuditQuery? _audit;

    public ActionRunService(
        IActionSet actionSet,
        ITraceSink trace,
        IActionRunStore store,
        IAuditQuery? audit = null)
    {
        _actionSet = actionSet;
        _gateway = FailClosedActionGateway.FromActionSet(actionSet, trace);
        _trace = trace;
        _store = store;
        _audit = audit;
    }

    public string ActionSetName => _actionSet.Name;

    /// <summary>同步审批路径：审批由注入口当场给结论。</summary>
    public async ValueTask<ActionResult> RunAsync(ActionRequest request, CancellationToken ct)
    {
        var blocked = await GateAsync(request, ct);
        if (blocked is not null)
            return blocked;

        var spec = await _actionSet.Specs.FindAsync(request.ActionKey, ct);
        if (spec is null)
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.SpecNotFound);

        string? approver = null;
        if (spec.SideEffecting)
        {
            await _store.SuspendAsync(
                request.RequestId,
                ActionRunStates.PendingApproval,
                "gate=validated;stage=awaiting-approval",
                ct);
            var decision = await _actionSet.Approvals.RequestAsync(request, ct);
            if (!decision.Approved)
            {
                await WriteAsync(request, "approval", "deny", decision.Reason, null, null, ct);
                return ActionResults.Reject(request.RequestId, ActionRejectionCodes.ApprovalDenied);
            }

            approver = decision.Approver;
            await _store.ResumeAsync(request.RequestId, ct);
            await WriteAsync(request, "approval", "allow", decision.Reason, approver, null, ct);
        }

        return await ExecuteAsync(spec, request, approver, ct);
    }

    /// <summary>
    /// 两阶段提交：先做四道校验，SideEffecting 动作挂起等人工，返回 Pending；
    /// 非副作用动作直接执行并返回终态。挂起后必须由 Approve / Reject 收口。
    /// </summary>
    public async ValueTask<ActionResult> SubmitDeferredAsync(ActionRequest request, CancellationToken ct)
    {
        var blocked = await GateAsync(request, ct);
        if (blocked is not null)
            return blocked;

        var spec = await _actionSet.Specs.FindAsync(request.ActionKey, ct);
        if (spec is null)
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.SpecNotFound);

        if (!spec.SideEffecting)
            return await ExecuteAsync(spec, request, null, ct);

        await _store.SuspendAsync(
            request.RequestId,
            ActionRunStates.PendingApproval,
            PendingRequestCodec.Encode(request),
            ct);
        await WriteAsync(request, "approval", "pending", "waiting for human approval", null, null, ct);

        return new ActionResult(
            request.RequestId,
            ActionStatuses.Pending,
            ActionRejectionCodes.ApprovalRequired,
            null,
            null,
            ActionResults.Empty);
    }

    /// <summary>人工同意后恢复执行。返回 null 表示没有该 request_id 的待审记录。</summary>
    public async ValueTask<ActionResult?> ApproveAsync(string requestId, string approver, CancellationToken ct)
    {
        var request = await LoadPendingAsync(requestId, ct);
        if (request is null)
            return null;

        var spec = await _actionSet.Specs.FindAsync(request.ActionKey, ct);
        if (spec is null)
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.SpecNotFound);

        await _store.ResumeAsync(requestId, ct);
        await WriteAsync(request, "approval", "allow", "approved by human", approver, null, ct);
        return await ExecuteAsync(spec, request, approver, ct);
    }

    /// <summary>人工拒绝：零外部调用直接进终态。</summary>
    public async ValueTask<ActionResult?> RejectAsync(string requestId, string reason, CancellationToken ct)
    {
        var request = await LoadPendingAsync(requestId, ct);
        if (request is null)
            return null;

        await WriteAsync(request, "approval", "deny", reason, null, null, ct);
        var result = ActionResults.Reject(requestId, ActionRejectionCodes.ApprovalDenied);
        await _store.SaveResultAsync(result, ct);
        return result;
    }

    public ValueTask<IReadOnlyList<TraceEvent>> AuditTrailAsync(string requestId, CancellationToken ct)
        => _audit is null
            ? ValueTask.FromResult<IReadOnlyList<TraceEvent>>(Array.Empty<TraceEvent>())
            : _audit.AuditTrailAsync(requestId, ct);

    /// <summary>待办详情：只解码已挂起的输入快照，不解读业务字段含义。</summary>
    public ValueTask<ActionRequest?> LoadPendingRequestAsync(string requestId, CancellationToken ct)
        => LoadPendingAsync(requestId, ct);

    private async ValueTask<ActionRequest?> LoadPendingAsync(string requestId, CancellationToken ct)
    {
        var checkpoint = await _store.LoadCheckpointAsync(requestId, ct);
        if (checkpoint is null ||
            !string.Equals(checkpoint.State, ActionRunStates.PendingApproval, StringComparison.Ordinal))
            return null;

        // 解码失败按「不可恢复」处理，不猜参数继续执行。
        return PendingRequestCodec.TryDecode(checkpoint.ProgressBlob);
    }

    /// <summary>幂等回放 + 四道校验。返回 null 表示可以继续；否则就是终态。</summary>
    private async ValueTask<ActionResult?> GateAsync(ActionRequest request, CancellationToken ct)
    {
        var prior = await _store.LoadResultAsync(request.RequestId, ct);
        if (prior is not null)
            return prior with { Code = ActionSuccessCodes.ReplayedFromIdempotency };

        var gate = await _gateway.SubmitAsync(request, ct);
        return gate.Status == ActionStatuses.Validated ? null : gate;
    }

    private async ValueTask<ActionResult> ExecuteAsync(
        ActionSpec spec,
        ActionRequest request,
        string? approver,
        CancellationToken ct)
    {
        // 幂等领取：同 request_id 只允许一次副作用。
        if (!await _store.TryClaimIdempotencyAsync(request.RequestId, request.IdempotencyKey, ct))
        {
            var existing = await _store.LoadResultAsync(request.RequestId, ct);
            return existing ?? ActionResults.Reject(request.RequestId, ActionRejectionCodes.DuplicateClaim);
        }

        var result = await _actionSet.Executor.ExecuteAsync(spec, request, ct);

        // 超时 / 状态未知 → 只查证，不重放。写动作永远不在这里被调用第二次。
        if (result.Status is ActionStatuses.Unknown or ActionStatuses.Timeout)
        {
            var verified = await _actionSet.Executor.ExecuteAsync(_actionSet.QuerySpec, request, ct);
            result = verified.Status == ActionStatuses.Succeeded
                ? new ActionResult(
                    request.RequestId,
                    ActionStatuses.Succeeded,
                    ActionSuccessCodes.VerifiedByQuery,
                    verified.ExternalId,
                    null,
                    verified.Data)
                : ActionResults.Fail(request.RequestId, ActionRejectionCodes.Timeout);
        }

        await WriteAsync(request, AuditStages.Execute, result.Status, result.Code, approver, result.ExternalId, ct);
        await _store.SaveResultAsync(result, ct);
        return result;
    }

    private async ValueTask WriteAsync(
        ActionRequest request,
        string stage,
        string outcome,
        string? result,
        string? approver,
        string? externalId,
        CancellationToken ct)
    {
        await _trace.WriteAsync(
            TraceEvents.Create(request, stage, outcome, result, approver, externalId), ct);
    }
}
