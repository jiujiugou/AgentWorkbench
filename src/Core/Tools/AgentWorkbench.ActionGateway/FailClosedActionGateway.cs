namespace AgentWorkbench.ActionGateway;

using AgentWorkbench.Ports;

/// <summary>
/// 四道校验网关（M1 设计 §4）：Schema → Permission → BusinessRule → Audit，严格按序。
/// 任一不过返回 Rejected，并且不触碰任何执行体；轨迹写不进去同样拒绝（fail-closed）。
/// 本类只做「校验结论」，不调用 IActionExecutor —— 执行在审批通过之后由编排层发起，
/// 这样「四道不过时 executor 零调用」是可被测试证伪的真实行为，而不是结构上恒真。
/// </summary>
public sealed class FailClosedActionGateway : IActionGateway
{
    private readonly IActionSpecRegistry _specs;
    private readonly ISchemaValidator _schema;
    private readonly IPermissionQuery _permissions;
    private readonly IBusinessRule _rules;
    private readonly ITraceSink _trace;

    public FailClosedActionGateway(
        IActionSpecRegistry specs,
        ISchemaValidator schema,
        IPermissionQuery permissions,
        IBusinessRule rules,
        ITraceSink trace)
    {
        _specs = specs;
        _schema = schema;
        _permissions = permissions;
        _rules = rules;
        _trace = trace;
    }

    public static FailClosedActionGateway FromActionSet(IActionSet actionSet, ITraceSink trace)
        => new(actionSet.Specs, actionSet.Schema, actionSet.Permissions, actionSet.Rules, trace);

    /// <summary>通过四道校验的结论：Status=Validated（非终态），Code=validated。</summary>
    public async ValueTask<ActionResult> SubmitAsync(ActionRequest request, CancellationToken ct)
    {
        var spec = await _specs.FindAsync(request.ActionKey, ct);
        if (spec is null)
        {
            await TraceAsync(request, AuditStages.Schema, "deny", ActionRejectionCodes.SpecNotFound, null, ct);
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.SpecNotFound);
        }

        // 道 1：Schema
        var schema = await _schema.ValidateAsync(spec, request, ct);
        if (!await TraceAsync(request, AuditStages.Schema, schema.Valid ? "allow" : "deny", schema.Reason, null, ct))
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.AuditWriteFailed);
        if (!schema.Valid)
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.SchemaInvalid);

        // 道 2：唯一 PDP
        var permission = await _permissions.QueryAsync(
            new PermissionQuery(request.Subject, request.ActionKey, spec.ActionKey), ct);
        if (!await TraceAsync(request, AuditStages.Permission, permission.Allow ? "allow" : "deny", permission.Reason, null, ct))
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.AuditWriteFailed);
        if (!permission.Allow)
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.PermissionDenied);

        // 道 3：动作域注入的业务判据
        var rule = await _rules.EvaluateAsync(request, ct);
        if (!await TraceAsync(request, AuditStages.BusinessRule, rule.Allow ? "allow" : "deny", rule.Reason, null, ct))
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.AuditWriteFailed);
        if (!rule.Allow)
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.BusinessRuleDenied);

        // 道 4：审计落痕本身必须成功
        if (!await TraceAsync(request, AuditStages.Audit, "written", "gateway passed", null, ct))
            return ActionResults.Reject(request.RequestId, ActionRejectionCodes.AuditWriteFailed);

        return new ActionResult(
            request.RequestId,
            ActionStatuses.Validated,
            "validated",
            null,
            null,
            ActionResults.Empty);
    }

    /// <summary>写一条阶段轨迹；写入失败返回 false，调用方必须据此拒绝，不得静默放行。</summary>
    private async ValueTask<bool> TraceAsync(
        ActionRequest request,
        string stage,
        string outcome,
        string? result,
        string? approver,
        CancellationToken ct)
    {
        var trace = TraceEvents.Create(request, stage, outcome, result, approver, null);
        var write = await _trace.WriteAsync(trace, ct);
        return write.Written;
    }
}
