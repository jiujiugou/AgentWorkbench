namespace AgentWorkbench.Ports;

/// <summary>
/// 拒绝 / 终态原因码。与 <see cref="ActionStatuses"/> 分开：Status 是终态，Code 是归因。
/// 每个拒绝都能归因到唯一一个 Code（RR-1），拒绝原因不与 unhandled_error 混。
/// </summary>
public static class ActionRejectionCodes
{
    public const string SpecNotFound = "spec_not_found";
    public const string SchemaInvalid = "schema_invalid";
    public const string PermissionDenied = "permission_denied";
    public const string BusinessRuleDenied = "business_rule_denied";
    public const string AuditWriteFailed = "audit_write_failed";
    public const string ExecutorMissing = "executor_missing";
    public const string ApprovalRequired = "approval_required";
    public const string ApprovalDenied = "approval_denied";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string Timeout = "timeout";
    public const string DuplicateClaim = "duplicate_claim";
}

/// <summary>成功终态的 Code 取值。</summary>
public static class ActionSuccessCodes
{
    public const string Completed = "completed";
    public const string ReplayedFromIdempotency = "replayed_from_idempotency";
    public const string VerifiedByQuery = "verified_by_query";
}
