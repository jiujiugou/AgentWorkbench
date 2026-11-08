namespace AgentWorkbench.Erp.ErpNext;

/// <summary>
/// HTTP 层的一次结果。刻意不用 <c>HttpResponseMessage</c> 往外传：
/// 那玩意释放语义容易出错，而且传输层不该把 HttpClient 的细节泄漏给业务解析。
/// </summary>
/// <param name="StatusCode">0 表示根本没拿到响应（连接失败 / 超时）。</param>
/// <param name="TransportError">非空表示网络层失败，原因为原始异常摘要。</param>
/// <param name="TimedOut">超时与「调用方主动取消」必须分开，否则无法区分 Unknown 与用户中断。</param>
public sealed record ErpHttpResponse(
    int StatusCode,
    string Body,
    TimeSpan Elapsed,
    string? TransportError = null,
    bool TimedOut = false)
{
    public bool Ok => StatusCode is >= 200 and < 300;
}

/// <summary>
/// 只做 HTTP，不含业务语义（M2 设计 §3.1 决定 1）。
/// 抽出这一层不是为了「可测试」这种套话，而是为了让同一套解析既能跑真网络、
/// 也能跑按 Frappe 协议实现的替身 —— 否则回放绿了也证明不了真接。
/// </summary>
public interface IErpTransport
{
    ValueTask<ErpHttpResponse> GetAsync(
        string path,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken ct);
}
