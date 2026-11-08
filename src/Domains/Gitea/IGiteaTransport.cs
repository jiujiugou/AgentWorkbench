namespace AgentWorkbench.Gitea;

/// <summary>HTTP 层的最小结果。刻意不返回响应头 —— 头里可能有 Set-Cookie 等不该进审计的东西。</summary>
public sealed record GiteaResponse(int StatusCode, string Body, string? TransportError = null)
{
    public bool IsTransportFailure => TransportError is not null;
}

/// <summary>
/// 传输抽象（方案第 2 步 Connector 的可替换半边）。
/// 只发请求、拿回状态码与正文；凭据、重试、超时与失败分类都封装在实现里，
/// 不泄漏给 Adapter，也不写入任何 TraceEvent。
/// </summary>
public interface IGiteaTransport
{
    ValueTask<GiteaResponse> SendAsync(string method, string path, string? jsonBody, CancellationToken ct);
}
