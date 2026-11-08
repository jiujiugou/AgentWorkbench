namespace AgentWorkbench.Erp.ErpNext;

using System.Net;

/// <summary>
/// 真网络实现。三条语义必须和 M2 设计 §6 一致：
/// 1. 读可以重试（5xx / 429 / 超时 / 连接失败），写不能重试 —— 写不在本类；
/// 2. 401 / 403 / 404 一律不重试（重试等于拿凭据去爆破，或者把「查不到」变成延迟报错）；
/// 3. 超时要能和「调用方取消」区分开，否则上层没法决定 Unknown 还是直接放弃。
/// </summary>
public sealed class HttpErpTransport : IErpTransport
{
    private readonly HttpClient _http;
    private readonly ErpNextOptions _options;
    private readonly TimeSpan _retryBaseDelay;

    public HttpErpTransport(HttpClient http, ErpNextOptions options, TimeSpan? retryBaseDelay = null)
    {
        _http = http;
        _options = options;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromMilliseconds(200);
    }

    public async ValueTask<ErpHttpResponse> GetAsync(
        string path,
        IReadOnlyDictionary<string, string?>? query,
        CancellationToken ct)
    {
        var url = BuildUrl(path, query);
        var attempt = 0;
        var timeoutRetried = false;

        while (true)
        {
            var response = await SendOnceAsync(url, ct);

            var retry = response.TimedOut
                ? !timeoutRetried && attempt < 1          // 超时只再试一次
                : (response.StatusCode is 429 or >= 500 || response.TransportError is not null)
                  && attempt < _options.MaxReadRetries;   // 5xx / 429 / 连接失败

            if (!retry)
                return response;

            if (response.TimedOut) timeoutRetried = true;
            attempt++;
            await Task.Delay(_retryBaseDelay * attempt, ct);
        }
    }

    private async ValueTask<ErpHttpResponse> SendOnceAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(
            "Authorization", $"token {_options.ApiKey}:{_options.ApiSecret}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.EffectiveTimeout);

        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await _http.SendAsync(request, timeoutCts.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            started.Stop();
            return new ErpHttpResponse((int)response.StatusCode, body, started.Elapsed);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 只有「我们自己设的超时」才归 Unknown；调用方取消必须原样抛出去。
            started.Stop();
            return new ErpHttpResponse(0, string.Empty, started.Elapsed, "timeout waiting for ERP", TimedOut: true);
        }
        catch (HttpRequestException ex)
        {
            started.Stop();
            return new ErpHttpResponse(0, string.Empty, started.Elapsed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private string BuildUrl(string path, IReadOnlyDictionary<string, string?>? query)
    {
        var baseUrl = _options.BaseUrl!.TrimEnd('/');
        var url = baseUrl + path;
        if (query is null || query.Count == 0)
            return url;

        var pairs = query
            .Where(kv => kv.Value is not null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}");
        return url + "?" + string.Join("&", pairs);
    }
}
