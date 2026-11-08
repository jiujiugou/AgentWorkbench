namespace AgentWorkbench.ActionGateway;

using AgentWorkbench.Ports;

/// <summary>
/// 一次动作 run 的编排（M1 设计 §2.2 六步 / §2.4 状态机 / §6 幂等与审计）。
/// 输入必须是结构化 ActionRequest —— 模型只负责把自然语言翻成它，本类不依赖模型，
/// 因此「拿掉模型系统仍能安全拒绝」在结构上成立，而不是靠提示词保证。
///
/// 顺序铁律：四道校验 → 挂起审批（仅 SideEffecting）→ 幂等领取 → 执行 → 收口。
/// 校验未过 / 审批未通过时，IActionExecutor 一次都不会被调用。
/// 超时或状态未知时只发起 Query 查证，绝不重放写动作。
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

    public async ValueTask<ActionResult> RunAsync(ActionRequest request, CancellationToken ct)
    {
        // 步骤 1：收请求。request_id 由 Host 生成并贯穿全程；同一 request_id 已有终态直接回放。
        var prior = await _store.LoadResultAsync(request.RequestId, ct);
        if (prior is not null)
            return prior with { Code = ActionSuccessCodes.ReplayedFromIdempotency };

        // 步骤 2：意图结构化已是输入前提（模型只做翻译）。此处不接受自由文本。
        // 步骤 3：四道校验，fail-closed。
        var gate = await _gateway.SubmitAsync(request, ct);
        if (gate.Status != ActionStatuses.Validated)
            return gate;

        var spec = await _actionSet.Specs.FindAsync(request.ActionKey, ct);
        if (spec is null)
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.SpecNotFound);

        // 步骤 4：仅 SideEffecting 动作挂起审批。Checkpoint 只存执行进度，不存业务状态。
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

        // 幂等领取：同 request_id 只允许一次副作用，去重表在底座侧。
        if (!await _store.TryClaimIdempotencyAsync(request.RequestId, request.IdempotencyKey, ct))
        {
            var existing = await _store.LoadResultAsync(request.RequestId, ct);
            return existing ?? ActionResults.Reject(request.RequestId, ActionRejectionCodes.DuplicateClaim);
        }

        // 步骤 5：执行四原语之一。
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

        // 步骤 6：收口。审计五字段（Intent / Arguments / Approver / Result / ExternalId）落痕，终态缓存。
        await WriteAsync(request, AuditStages.Execute, result.Status, result.Code, approver, result.ExternalId, ct);
        await _store.SaveResultAsync(result, ct);
        return result;
    }

    public ValueTask<IReadOnlyList<TraceEvent>> AuditTrailAsync(string requestId, CancellationToken ct)
        => _audit is null
            ? ValueTask.FromResult<IReadOnlyList<TraceEvent>>(Array.Empty<TraceEvent>())
            : _audit.AuditTrailAsync(requestId, ct);

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
