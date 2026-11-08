namespace AgentWorkbench.ActionGateway;

using AgentWorkbench.Ports;

/// <summary>挂起期间保存的待执行意图快照。</summary>
internal sealed record PendingRequest(
    string RequestId,
    string Subject,
    string ActionKey,
    string ArgumentsJson,
    string? IdempotencyKey);

/// <summary>
/// 把已通过四道校验的 ActionRequest 编成 Checkpoint 的 ProgressBlob，供人工审批后恢复执行。
///
/// 边界说明（ADR 0017 §四）：这里存的是**框架执行进度**（待恢复的输入、已过的校验阶段），
/// 不是业务单据状态。单据状态在业务库，两者不混写。参数对底座仍是原始 JSON 文本，不解析字段含义。
/// </summary>
public static class PendingRequestCodec
{
    public static string Encode(ActionRequest request)
        => JsonSerializer.Serialize(new PendingRequest(
            request.RequestId,
            request.Subject,
            request.ActionKey,
            TraceEvents.RawArguments(request),
            request.IdempotencyKey));

    /// <summary>解码失败返回 null，调用方按「checkpoint 不可用」处理，不得猜参数继续执行。</summary>
    public static ActionRequest? TryDecode(string? progressBlob)
    {
        if (string.IsNullOrWhiteSpace(progressBlob))
            return null;

        PendingRequest? pending;
        try
        {
            pending = JsonSerializer.Deserialize<PendingRequest>(progressBlob);
        }
        catch (JsonException)
        {
            return null;
        }

        if (pending is null || string.IsNullOrWhiteSpace(pending.RequestId) || string.IsNullOrWhiteSpace(pending.ActionKey))
            return null;

        using var doc = JsonDocument.Parse(pending.ArgumentsJson);
        return new ActionRequest(
            pending.RequestId,
            pending.Subject,
            pending.ActionKey,
            doc.RootElement.Clone(),
            pending.IdempotencyKey);
    }
}
