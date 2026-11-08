namespace AgentWorkbench.Ports;

/// <summary>注入口 1：底座问「能不能做」，不理解判据内容。唯一 PDP 在企业应用层。</summary>
public sealed record PermissionQuery(string Subject, string ActionKey, string Scope);

/// <summary>权限判定。Reason 供审计留痕，不参与决策逻辑。</summary>
public sealed record PermissionDecision(bool Allow, string Reason);

/// <summary>第 1 道：Schema 校验结果。Schema 本体由动作域提供，底座只按 SchemaId 取用。</summary>
public sealed record SchemaDecision(bool Valid, string Reason);

/// <summary>
/// 注入口 2：业务规则判定。<c>Evidence</c> 是条款/依据快照的引用（片段 id、文件名、序号、页码），
/// 由动作域注入，落在 C# 侧；底座不复制正文、不解读内容（M1 设计 §6）。
/// </summary>
public sealed record RuleDecision(bool Allow, string Reason, IReadOnlyList<EvidenceRef>? Evidence);

/// <summary>证据快照引用。抽象知识层词汇，不含业务语义。</summary>
public sealed record EvidenceRef(string FragmentId, string FileName, int Ordinal, int? Page);

/// <summary>
/// 四道校验的 Stage 名，同时是 <see cref="TraceEvent.Stage"/> 的取值。
/// 按序：Schema → Permission → BusinessRule → Audit（M1 设计 §4）。
/// </summary>
public static class AuditStages
{
    public const string Schema = "schema";
    public const string Permission = "permission";
    public const string BusinessRule = "business_rule";
    public const string Audit = "audit";
    public const string Execute = "execute";
}

/// <summary>第 1 道校验：参数是否符合 ActionSpec.SchemaId 指向的 Schema。</summary>
public interface ISchemaValidator
{
    ValueTask<SchemaDecision> ValidateAsync(ActionSpec spec, ActionRequest request, CancellationToken ct);
}

/// <summary>第 2 道校验：唯一 PDP。底座只拿 allow/deny，不认识金额、层级等判据。</summary>
public interface IPermissionQuery
{
    ValueTask<PermissionDecision> QueryAsync(PermissionQuery query, CancellationToken ct);
}

/// <summary>第 3 道校验：判据、题库、通过线由动作域注入。</summary>
public interface IBusinessRule
{
    ValueTask<RuleDecision> EvaluateAsync(ActionRequest request, CancellationToken ct);
}

/// <summary>第 4 道校验：审计留痕。写不进去必须拒绝（fail-closed）。</summary>
public interface ITraceSink
{
    ValueTask<AuditWriteResult> WriteAsync(TraceEvent trace, CancellationToken ct);
}

/// <summary>留痕结果。false 时网关必须拒绝，不得继续放行。</summary>
public sealed record AuditWriteResult(bool Written, string Reason);

/// <summary>
/// 底座侧网关：四道校验按序执行，任何一道不过，<see cref="IActionExecutor"/> 一次都不能被调用。
/// 默认实现必须全部返回拒绝，不提供任何「放行」默认（M1 设计 §4）。
/// </summary>
public interface IActionGateway
{
    ValueTask<ActionResult> SubmitAsync(ActionRequest request, CancellationToken ct);
}

/// <summary>适配器半边：动作域实现，只拿已校验参数；不负责权限、幂等、审计。</summary>
public interface IActionExecutor
{
    ValueTask<ActionResult> ExecuteAsync(ActionSpec spec, ActionRequest request, CancellationToken ct);
}

/// <summary>动作类型注册表：底座按 ActionKey 取规格，实例由动作域注入。</summary>
public interface IActionSpecRegistry
{
    ValueTask<ActionSpec?> FindAsync(string actionKey, CancellationToken ct);
}
