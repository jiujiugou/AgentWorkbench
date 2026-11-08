namespace AgentWorkbench.ActionGateway;

using AgentWorkbench.Ports;

/// <summary>
/// 默认全拒绝组件（M1 设计 §4「不提供任何放行默认」）。
/// 这些类型只用于零配置组装与「拿掉模型仍安全拒绝」的验证，生产组合根必须注入动作域实现。
/// </summary>
public sealed class AnyActionSpecRegistry : IActionSpecRegistry
{
    public ValueTask<ActionSpec?> FindAsync(string actionKey, CancellationToken ct)
        => ValueTask.FromResult<ActionSpec?>(
            new ActionSpec(actionKey, "default", true, ActionPrimitives.Submit)
            {
                DisplayName = "未注册动作",
                Description = "零配置占位规格。四道校验默认全拒绝，本规格不应被真正执行。",
                RiskLevel = RiskLevels.Critical,
                RequiresApproval = true,
                Version = "0",
                InputSchemaJson = "{}",
            });

    public ValueTask<IReadOnlyList<ActionSpec>> ListAsync(CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyList<ActionSpec>>([]);
}

public sealed class DenyAllSchemaValidator : ISchemaValidator
{
    public ValueTask<SchemaDecision> ValidateAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
        => ValueTask.FromResult(new SchemaDecision(false, "no schema validator registered; fail-closed"));
}

public sealed class DenyAllPermissionQuery : IPermissionQuery
{
    public ValueTask<PermissionDecision> QueryAsync(PermissionQuery query, CancellationToken ct)
        => ValueTask.FromResult(new PermissionDecision(false, "no PDP registered; fail-closed"));
}

public sealed class DenyAllBusinessRule : IBusinessRule
{
    public ValueTask<RuleDecision> EvaluateAsync(ActionRequest request, CancellationToken ct)
        => ValueTask.FromResult(new RuleDecision(false, "no business rule registered; fail-closed", null));
}

/// <summary>轨迹通道不可用的模拟：写入失败必须导致拒绝，不得静默放行。</summary>
public sealed class UnavailableTraceSink : ITraceSink
{
    public ValueTask<AuditWriteResult> WriteAsync(TraceEvent trace, CancellationToken ct)
        => ValueTask.FromResult(new AuditWriteResult(false, "trace sink unavailable; fail-closed"));
}

public sealed class NoExecutor : IActionExecutor
{
    public int CallCount { get; private set; }

    public ValueTask<ActionResult> ExecuteAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
    {
        CallCount++;
        return ValueTask.FromResult(ActionResults.Reject(request.RequestId, ActionRejectionCodes.ExecutorMissing));
    }
}

/// <summary>
/// 零配置装配：四道校验与轨迹通道全部拒绝，执行体为空。
/// 拿掉模型、让模型吐畸形 JSON、或什么都不注入，结果都是 Rejected，且 executor 零调用。
/// </summary>
public static class FailClosedDefaults
{
    public static FailClosedActionGateway Create(ITraceSink? trace = null)
        => new(
            new AnyActionSpecRegistry(),
            new DenyAllSchemaValidator(),
            new DenyAllPermissionQuery(),
            new DenyAllBusinessRule(),
            trace ?? new UnavailableTraceSink());
}
