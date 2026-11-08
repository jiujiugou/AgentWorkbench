namespace AgentWorkbench.LabBooking;

using AgentWorkbench.Ports;

internal sealed record LabBooking(
    string ExternalId,
    string RequestId,
    string LabId,
    decimal SlotHours,
    decimal Attendees,
    string State);

internal static class LabResults
{
    private static JsonElement Data(object payload) => JsonSerializer.SerializeToElement(payload);

    public static ActionResult Succeeded(string requestId, string? externalId, object payload)
        => new(requestId, ActionStatuses.Succeeded, "completed", externalId, null, Data(payload));

    public static ActionResult Unknown(string requestId, string? externalId, string reason)
        => new(requestId, ActionStatuses.Unknown, ActionRejectionCodes.Timeout,
            externalId, ActionRejectionCodes.Timeout, Data(new { state = "unknown", reason }));

    public static ActionResult Failed(string requestId, string code, string reason)
        => new(requestId, ActionStatuses.Failed, code, null, code,
            Data(new { state = "failed", reason }));
}

/// <summary>
/// 假实验室预约：四原语的内存实现（M1 设计 §5.2）。既是网关单测装置，也是 AC-01 换域演练载体。
/// - Draft 可重复、无副作用；
/// - Submit 按 request_id 去重，同 request_id 只生成一张 LB-2026-XXXX 预约单；
/// - 超时模拟：单据已落库为 Submitted 但响应丢失，返回 Unknown；恢复只靠 Query 查证，永不重放写动作；
/// - Cancel 只作用于未提交状态的单据。
/// </summary>
public sealed class InMemoryFakeLab : IActionExecutor
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LabBooking> _byRequestId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LabBooking> _byExternalId = new(StringComparer.Ordinal);
    private int _sequence;

    public bool SimulateTimeoutOnNextSubmit { get; set; }
    public int SubmitCallCount { get; private set; }
    public int QueryCallCount { get; private set; }
    public int DraftCallCount { get; private set; }
    public int CancelCallCount { get; private set; }

    public int DocumentCount
    {
        get { lock (_gate) return _byExternalId.Count; }
    }

    public ValueTask<ActionResult> ExecuteAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            return spec.Primitive switch
            {
                ActionPrimitives.Query => ValueTask.FromResult(DoQuery(request)),
                ActionPrimitives.Draft => ValueTask.FromResult(DoDraft(request)),
                ActionPrimitives.Submit => ValueTask.FromResult(DoSubmit(request)),
                ActionPrimitives.Cancel => ValueTask.FromResult(DoCancel(request)),
                _ => ValueTask.FromResult(LabResults.Failed(request.RequestId, "unknown_primitive", spec.Primitive)),
            };
        }
    }

    private ActionResult DoQuery(ActionRequest request)
    {
        QueryCallCount++;
        if (_byRequestId.TryGetValue(request.RequestId, out var doc))
            return LabResults.Succeeded(request.RequestId, doc.ExternalId,
                new
                {
                    state = doc.State,
                    booking_no = doc.ExternalId,
                    lab_id = doc.LabId,
                    slot_hours = doc.SlotHours,
                    attendees = doc.Attendees,
                });
        return LabResults.Failed(request.RequestId, "not_found", "查无此预约单");
    }

    private ActionResult DoDraft(ActionRequest request)
    {
        DraftCallCount++;
        LabArguments.TryReadLabId(request, out var labId);
        LabArguments.TryReadSlotHours(request, out var slotHours);
        LabArguments.TryReadAttendees(request, out var attendees);
        // Draft 不写状态、不生成单号，重复调用无副作用。
        return LabResults.Succeeded(request.RequestId, null,
            new { state = "draft", lab_id = labId, slot_hours = slotHours, attendees, preview = true });
    }

    private ActionResult DoSubmit(ActionRequest request)
    {
        SubmitCallCount++;
        if (_byRequestId.TryGetValue(request.RequestId, out var existing))
            return LabResults.Succeeded(request.RequestId, existing.ExternalId,
                new { state = existing.State, booking_no = existing.ExternalId, lab_id = existing.LabId, duplicated = true });

        LabArguments.TryReadLabId(request, out var labId);
        LabArguments.TryReadSlotHours(request, out var slotHours);
        LabArguments.TryReadAttendees(request, out var attendees);
        var externalId = $"LB-2026-{++_sequence:D4}";
        var doc = new LabBooking(externalId, request.RequestId, labId, slotHours, attendees, "Submitted");
        _byRequestId[request.RequestId] = doc;
        _byExternalId[externalId] = doc;

        if (SimulateTimeoutOnNextSubmit)
        {
            SimulateTimeoutOnNextSubmit = false;
            // 单据照常落库为 Submitted，但响应丢失：状态未知，只能靠 Query 查证，绝不重放写动作。
            return LabResults.Unknown(request.RequestId, externalId, "外部写已发出，响应丢失，状态未知");
        }

        return LabResults.Succeeded(request.RequestId, externalId,
            new { state = doc.State, booking_no = externalId, lab_id = labId, slot_hours = slotHours, attendees });
    }

    private ActionResult DoCancel(ActionRequest request)
    {
        CancelCallCount++;
        if (!_byRequestId.TryGetValue(request.RequestId, out var doc))
            return LabResults.Failed(request.RequestId, "not_found", "查无此预约单");
        if (string.Equals(doc.State, "Submitted", StringComparison.Ordinal))
            return LabResults.Failed(request.RequestId, "already_submitted", "已提交预约不可撤销");

        var cancelled = doc with { State = "Cancelled" };
        _byRequestId[request.RequestId] = cancelled;
        _byExternalId[cancelled.ExternalId] = cancelled;
        return LabResults.Succeeded(request.RequestId, cancelled.ExternalId,
            new { state = cancelled.State, booking_no = cancelled.ExternalId });
    }

    public void Reset()
    {
        lock (_gate)
        {
            _byRequestId.Clear();
            _byExternalId.Clear();
            _sequence = 0;
            SubmitCallCount = 0;
            QueryCallCount = 0;
            DraftCallCount = 0;
            CancelCallCount = 0;
            SimulateTimeoutOnNextSubmit = false;
        }
    }
}
