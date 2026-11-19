namespace AgentWorkbench.ActionGateway;

using AgentWorkbench.Ports;

/// <summary>ActionResult 的底座侧构造助手。Data 恒为空对象，业务内容由动作域填。</summary>
public static class ActionResults
{
    private static readonly JsonElement EmptyData = CreateEmptyData();

    private static JsonElement CreateEmptyData()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    public static JsonElement Empty => EmptyData;

    public static ActionResult Reject(string requestId, string code, string? errorCode = null)
        => new(requestId, ActionStatuses.Rejected, code, null, errorCode ?? code, EmptyData);

    public static ActionResult Fail(string requestId, string code, string? errorCode = null)
        => new(requestId, ActionStatuses.Failed, code, null, errorCode ?? code, EmptyData);
}
