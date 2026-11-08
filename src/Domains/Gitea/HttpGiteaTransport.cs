namespace AgentWorkbench.Gitea;

using System.Net.Http.Headers;
using System.Text;

/// <summary>
/// 真 HttpClient 实现。可注入 HttpMessageHandler 做协议级测试（只换 handler，其余管道全真）。
/// 三条实测约束：
/// 1. 认证头 `Authorization: token &lt;token&gt;`（Frappe 是 `token key:secret`，格式不同，别复用）。
/// 2. 401/403/404 一律不重试；只有 429/5xx/传输失败可重试，且<b>只对 GET</b>。
/// 3. 调用方取消与自己的超时分开：调用方取消必须传播，不能伪装成上游超时。
/// </summary>
public sealed class HttpGiteaTransport : IGiteaTransport, IDisposable
{
    private readonly HttpClient _client;
    private readonly int _maxReadRetries;

    public HttpGiteaTransport(GiteaOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxReadRetries = Math.Max(0, options.MaxReadRetries);

        var primary = handler ?? new HttpClientHandler { AllowAutoRedirect = false };
        _client = new HttpClient(primary, disposeHandler: handler is null)
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)),
        };
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // 凭据只存在于这一个头里。GiteaResponse 不携带头，TraceEvent 拿不到它。
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", options.Token);
    }

    public async ValueTask<GiteaResponse> SendAsync(
        string method, string path, string? jsonBody, CancellationToken ct)
    {
        // 调用方取消必须在发请求之前就传播出去，不能伪装成上游超时。
        ct.ThrowIfCancellationRequested();

        var verb = method.ToUpperInvariant();
        var retryableVerb = verb == "GET";
        var attempt = 0;

        while (true)
        {
            using var request = new HttpRequestMessage(new HttpMethod(verb), path.TrimStart('/'));
            if (jsonBody is not null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            GiteaResponse response;
            try
            {
                using var httpResponse = await _client.SendAsync(request, ct);
                var body = await httpResponse.Content.ReadAsStringAsync(ct);
                response = new GiteaResponse((int)httpResponse.StatusCode, body);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                response = new GiteaResponse(0, "", "timeout");
            }
            catch (HttpRequestException)
            {
                // 不把异常原文带上去：URL 与内部信息都可能含敏感内容。
                response = new GiteaResponse(0, "", "transport_error");
            }

            var shouldRetry = retryableVerb
                              && attempt < _maxReadRetries
                              && (response.StatusCode is 429 or >= 500 || response.IsTransportFailure);

            if (!shouldRetry)
                return response;

            attempt++;
        }
    }

    public void Dispose() => _client.Dispose();
}
