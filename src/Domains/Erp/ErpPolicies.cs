namespace AgentWorkbench.Erp;

using AgentWorkbench.Ports;

/// <summary>
/// 第 1 道：动作域提供的 JSON Schema 校验。底座只按 SchemaId 调用，不理解字段含义。
/// 这里做最小形状校验：document_id 必填；写动作额外要求 amount 为正数。
/// </summary>
public sealed class ErpSchemaValidator : ISchemaValidator
{
    public ValueTask<SchemaDecision> ValidateAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
    {
        var args = request.Arguments;
        if (args.ValueKind != JsonValueKind.Object)
            return ValueTask.FromResult(new SchemaDecision(false, "arguments must be a JSON object"));

        if (!args.TryGetProperty("document_id", out var documentId) ||
            documentId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(documentId.GetString()))
            return ValueTask.FromResult(new SchemaDecision(false, "document_id is required"));

        if (spec.Primitive is ActionPrimitives.Draft or ActionPrimitives.Submit)
        {
            if (!args.TryGetProperty("amount", out var amount) ||
                amount.ValueKind != JsonValueKind.Number ||
                amount.GetDecimal() <= 0)
                return ValueTask.FromResult(new SchemaDecision(false, "amount must be a positive number"));
        }

        return ValueTask.FromResult(new SchemaDecision(true, $"schema {spec.SchemaId} ok"));
    }
}

/// <summary>第 2 道：假 PDP。唯一权限判定点，底座只拿 allow/deny。</summary>
public sealed class ErpPermissionPdp : IPermissionQuery
{
    public bool DenyAll { get; set; }

    public ISet<string> AllowedSubjects { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "wang", "approver" };

    public ValueTask<PermissionDecision> QueryAsync(PermissionQuery query, CancellationToken ct)
    {
        var allow = !DenyAll && AllowedSubjects.Contains(query.Subject);
        return ValueTask.FromResult(new PermissionDecision(
            allow,
            allow ? "subject holds erp role" : "subject has no erp role"));
    }
}

/// <summary>
/// 第 3 道：假业务判据（《备件采购审批制度》档位）。
/// EvidenceRef 是条款快照引用，供「依据哪条规则」复核，底座不复制正文。
/// </summary>
public sealed class ErpBusinessRule : IBusinessRule
{
    public const decimal AmountCeiling = 50000m;

    public ValueTask<RuleDecision> EvaluateAsync(ActionRequest request, CancellationToken ct)
    {
        IReadOnlyList<EvidenceRef> evidence =
        [
            new EvidenceRef("clause-3", "备件采购审批制度.md", 3, 1),
            new EvidenceRef("clause-7", "备件采购审批制度.md", 7, 2),
        ];

        if (!ErpArguments.TryReadAmount(request, out var amount))
            return ValueTask.FromResult(new RuleDecision(false, "金额字段缺失或非法", null));

        var allow = amount <= AmountCeiling;
        return ValueTask.FromResult(new RuleDecision(
            allow,
            allow
                ? $"金额 {amount} 未超 {AmountCeiling} 档位，按第 3 条可由车间主任审批"
                : $"金额 {amount} 超 {AmountCeiling} 档位，按第 7 条须升级审批",
            evidence));
    }
}

/// <summary>审批闸：审批人 / 层级 / 时长由动作域注入，底座只拿 Approved 与 Approver。</summary>
public sealed class ErpApprovalGate : IApprovalGate
{
    public bool Approved { get; set; } = true;
    public string ApproverName { get; set; } = "车间主任·王";

    public ValueTask<ApprovalDecision> RequestAsync(ActionRequest request, CancellationToken ct)
        => ValueTask.FromResult(new ApprovalDecision(
            Approved,
            Approved ? ApproverName : null,
            Approved ? "人工审批通过" : "人工审批拒绝"));
}

/// <summary>动作域内部的参数读取 helper。底座不做这件事。</summary>
internal static class ErpArguments
{
    public static bool TryReadAmount(ActionRequest request, out decimal amount)
    {
        amount = 0;
        if (request.Arguments.ValueKind != JsonValueKind.Object)
            return false;
        return request.Arguments.TryGetProperty("amount", out var node)
               && node.ValueKind == JsonValueKind.Number
               && node.TryGetDecimal(out amount);
    }
}
