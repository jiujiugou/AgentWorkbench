namespace AgentWorkbench.ActionGateway;

using AgentWorkbench.Ports;

/// <summary>TraceEvent 的统一构造点。审计五字段由流程各步产出，这里只负责结构，不产生业务内容。</summary>
public static class TraceEvents
{
    public static TraceEvent Create(
        ActionRequest request,
        string stage,
        string outcome,
        string? result,
        string? approver,
        string? externalId)
        => new(
            request.RequestId,
            stage,
            outcome,
            request.ActionKey,
            RawArguments(request),
            approver,
            result,
            externalId,
            DateTimeOffset.UtcNow);

    public static string RawArguments(ActionRequest request)
        => request.Arguments.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : request.Arguments.GetRawText();
}
