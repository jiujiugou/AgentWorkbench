namespace AgentWorkbench.LabBooking;

using AgentWorkbench.Ports;

/// <summary>
/// 第 1 道：动作域提供的 JSON Schema 校验。底座只按 SchemaId 调用，不理解字段含义。
/// 最小形状校验：lab_id 必填；draft/submit 额外要求 slot_hours 与 attendees 为正数。
/// </summary>
public sealed class LabSchemaValidator : ISchemaValidator
{
    public ValueTask<SchemaDecision> ValidateAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
    {
        var args = request.Arguments;
        if (args.ValueKind != JsonValueKind.Object)
            return ValueTask.FromResult(new SchemaDecision(false, "arguments must be a JSON object"));

        if (!args.TryGetProperty("lab_id", out var labId) ||
            labId.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(labId.GetString()))
            return ValueTask.FromResult(new SchemaDecision(false, "lab_id is required"));

        if (spec.Primitive is ActionPrimitives.Draft or ActionPrimitives.Submit)
        {
            if (!args.TryGetProperty("slot_hours", out var slotHours) ||
                slotHours.ValueKind != JsonValueKind.Number ||
                slotHours.GetDecimal() <= 0)
                return ValueTask.FromResult(new SchemaDecision(false, "slot_hours must be a positive number"));

            if (!args.TryGetProperty("attendees", out var attendees) ||
                attendees.ValueKind != JsonValueKind.Number ||
                attendees.GetDecimal() <= 0)
                return ValueTask.FromResult(new SchemaDecision(false, "attendees must be a positive number"));
        }

        return ValueTask.FromResult(new SchemaDecision(true, $"schema {spec.SchemaId} ok"));
    }
}

/// <summary>第 2 道：假 PDP。唯一权限判定点，底座只拿 allow/deny。</summary>
public sealed class LabPermissionPdp : IPermissionQuery
{
    public bool DenyAll { get; set; }

    public ValueTask<PermissionDecision> QueryAsync(PermissionQuery query, CancellationToken ct)
    {
        var allow = !DenyAll &&
                    (query.Subject.EndsWith("@lab", StringComparison.Ordinal) ||
                     string.Equals(query.Subject, "wang", StringComparison.Ordinal));
        return ValueTask.FromResult(new PermissionDecision(
            allow,
            allow ? "subject holds lab booking role" : "subject has no lab booking role"));
    }
}

/// <summary>
/// 第 3 道：假业务判据（《实验室使用管理办法》时长与人数档位）。
/// EvidenceRef 是条款快照引用，供「依据哪条规则」复核，底座不复制正文。
/// </summary>
public sealed class LabBusinessRule : IBusinessRule
{
    public const decimal SlotHoursCeiling = 4m;
    public const decimal AttendeesCeiling = 12m;

    public ValueTask<RuleDecision> EvaluateAsync(ActionRequest request, CancellationToken ct)
    {
        IReadOnlyList<EvidenceRef> evidence =
        [
            new EvidenceRef("lab-clause-2", "实验室使用管理办法.md", 2, 1),
            new EvidenceRef("lab-clause-5", "实验室使用管理办法.md", 5, 2),
        ];

        if (!LabArguments.TryReadSlotHours(request, out var slotHours) ||
            !LabArguments.TryReadAttendees(request, out var attendees))
            return ValueTask.FromResult(new RuleDecision(false, "时长或人数字段缺失或非法", null));

        var allow = slotHours <= SlotHoursCeiling && attendees <= AttendeesCeiling;
        return ValueTask.FromResult(new RuleDecision(
            allow,
            allow
                ? $"时长 {slotHours} 小时、人数 {attendees} 人未超「时长与人数档位」，按第 2 条可由实验室管理员·李审批"
                : $"时长 {slotHours} 小时或人数 {attendees} 人超「时长与人数档位」，按第 5 条须升级审批",
            evidence));
    }
}

/// <summary>审批闸：审批人 / 层级 / 时长由动作域注入，底座只拿 Approved 与 Approver。</summary>
public sealed class LabApprovalGate : IApprovalGate
{
    public bool Approved { get; set; } = true;
    public string ApproverName { get; set; } = "实验室管理员·李";

    public ValueTask<ApprovalDecision> RequestAsync(ActionRequest request, CancellationToken ct)
        => ValueTask.FromResult(new ApprovalDecision(
            Approved,
            Approved ? ApproverName : null,
            Approved ? "人工审批通过" : "人工审批拒绝"));
}

/// <summary>动作域内部的参数读取 helper。底座不做这件事。</summary>
internal static class LabArguments
{
    public static bool TryReadLabId(ActionRequest request, out string labId)
    {
        labId = string.Empty;
        if (request.Arguments.ValueKind != JsonValueKind.Object)
            return false;
        if (!request.Arguments.TryGetProperty("lab_id", out var node) ||
            node.ValueKind != JsonValueKind.String)
            return false;
        labId = node.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(labId);
    }

    public static bool TryReadSlotHours(ActionRequest request, out decimal slotHours)
    {
        slotHours = 0;
        if (request.Arguments.ValueKind != JsonValueKind.Object)
            return false;
        return request.Arguments.TryGetProperty("slot_hours", out var node)
               && node.ValueKind == JsonValueKind.Number
               && node.TryGetDecimal(out slotHours);
    }

    public static bool TryReadAttendees(ActionRequest request, out decimal attendees)
    {
        attendees = 0;
        if (request.Arguments.ValueKind != JsonValueKind.Object)
            return false;
        return request.Arguments.TryGetProperty("attendees", out var node)
               && node.ValueKind == JsonValueKind.Number
               && node.TryGetDecimal(out attendees);
    }
}
