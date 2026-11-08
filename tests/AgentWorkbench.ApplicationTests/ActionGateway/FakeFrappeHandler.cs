using System.Net;
using System.Text;
using System.Text.Json;

namespace AgentWorkbench.ApplicationTests.ActionGateway;

/// <summary>
/// 协议级 ERPNext 替身（M2 设计 §3.4「仿真接入」）。
///
/// 为什么是 HTTP 层而不是 IActionExecutor 的 mock：
/// mock 掉执行体等于跳过 transport + adapter + 解析，只能验证编排，验证不了
/// 「frappe 的响应到底长什么样、401 和 404 怎么分、时间带不带时区」——
/// 那正是 M2 唯一的新信息。所以这里按 Frappe REST 的真实形状返回：
/// <c>{"data": {...}}</c> 信封、<c>exc_type</c> 错误体、naive 时间串、docstatus 0/1/2。
///
/// 它**不是**真实例：真实 Frappe 的响应是否与这里一致，仍要由 live 采集证明。
/// 这个替身能做的，是把「未验证的部分」从「一大片」收窄成一条。
/// </summary>
public sealed class FakeFrappeSite
{
    public string ApiKey { get; init; } = "test_key";
    public string ApiSecret { get; init; } = "test_secret";
    public string Doctype { get; init; } = "Purchase Order";

    public Dictionary<string, FakePurchaseOrder> Documents { get; } = new(StringComparer.Ordinal)
    {
        ["PO-M2-0001"] = FakePurchaseOrder.Draft("PO-M2-0001"),
        ["PO-M2-0002"] = FakePurchaseOrder.Submitted("PO-M2-0002"),
        ["PO-M2-0003"] = FakePurchaseOrder.Cancelled("PO-M2-0003"),
    };

    /// <summary>模拟 5xx / 429：所有单据请求都返回这个状态。</summary>
    public int? ForceStatus { get; set; }

    /// <summary>模拟字段漂移：返回体里去掉这个字段。</summary>
    public string? OmitField { get; set; }

    /// <summary>模拟 Frappe 把错误包在 200 里（exc_type + exception）。</summary>
    public bool ErrorPayloadWith200 { get; set; }

    /// <summary>模拟慢响应，配合短 Timeout 验超时分支。</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public int RequestCount { get; private set; }
    public string? LastPath { get; private set; }
    public string? LastAuthorization { get; private set; }

    internal void Record(string path, string? authorization)
    {
        RequestCount++;
        LastPath = path;
        LastAuthorization = authorization;
    }
}

public sealed record FakePurchaseOrder(
    string Name,
    int DocStatus,
    string Status,
    string Company,
    string Supplier,
    decimal GrandTotal,
    string Currency,
    string Modified,
    string TransactionDate = "2026-11-06",
    string Creation = "2026-11-05 08:00:00.000000",
    string Owner = "buyer@example.com")
{
    public static FakePurchaseOrder Draft(string name) => new(
        name, 0, "Draft", "华北智造", "河北备件", 48000m, "CNY", "2026-11-08 09:10:00.111111");

    public static FakePurchaseOrder Submitted(string name) => new(
        name, 1, "To Receive and Bill", "华北智造", "河北备件", 48000m, "CNY", "2026-11-08 10:00:00.123456");

    public static FakePurchaseOrder Cancelled(string name) => new(
        name, 2, "Cancelled", "华东自动化", "江苏机电", 600000m, "CNY", "2026-11-03 12:00:00.000000");
}

/// <summary>把 <see cref="FakeFrappeSite"/> 接到真实 HttpClient 上；走的是真 HttpClient 管道，只换了 handler。</summary>
public sealed class FakeFrappeHandler : HttpMessageHandler
{
    private readonly FakeFrappeSite _site;

    public FakeFrappeHandler(FakeFrappeSite site) => _site = site;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var rawPath = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        var auth = request.Headers.TryGetValues("Authorization", out var values) ? values.FirstOrDefault() : null;
        _site.Record(rawPath, auth);

        if (_site.Delay > TimeSpan.Zero)
            await Task.Delay(_site.Delay, ct);

        if (auth != $"token {_site.ApiKey}:{_site.ApiSecret}")
            return Json(401, new { exc_type = "AuthenticationError", message = "Invalid API key or secret" });

        if (rawPath == "/api/method/frappe.auth.get_logged_user")
            return Json(200, new { message = "m2-probe@example.com" });

        if (rawPath == "/api/method/frappe.client.get_single_value")
            return Json(200, new { message = "Asia/Shanghai" });

        if (rawPath == "/api/method/frappe.utils.change_log.get_versions")
            return Json(200, new { message = new { erpnext = new { version = "15.1.0" }, frappe = new { version = "15.0.0" } } });

        var docPrefix = $"/api/resource/{_site.Doctype}";
        if (rawPath == docPrefix)
            return Json(200, new { data = _site.Documents.Values.Select(d => new { d.Name, d.DocStatus, d.Status, d.Modified }) });

        if (rawPath.StartsWith(docPrefix + "/", StringComparison.Ordinal))
        {
            if (_site.ForceStatus is { } forced)
                return Json(forced, new { exc_type = "ServerError", message = $"forced status {forced}" });

            var name = rawPath[(docPrefix.Length + 1)..];
            if (!_site.Documents.TryGetValue(name, out var doc))
                return Json(404, new
                {
                    exc_type = "DoesNotExistError",
                    exception = $"frappe.exceptions.DoesNotExistError: {_site.Doctype} {name} not found",
                });

            if (_site.ErrorPayloadWith200)
                return Json(200, new { exc_type = "ValidationError", exception = "frappe.exceptions.ValidationError: broken link" });

            var payload = new Dictionary<string, object?>
            {
                ["name"] = doc.Name,
                ["docstatus"] = doc.DocStatus,
                ["status"] = doc.Status,
                ["company"] = doc.Company,
                ["supplier"] = doc.Supplier,
                ["grand_total"] = doc.GrandTotal,
                ["net_total"] = doc.GrandTotal,
                ["currency"] = doc.Currency,
                ["modified"] = doc.Modified,
                ["transaction_date"] = doc.TransactionDate,
                ["creation"] = doc.Creation,
                ["owner"] = doc.Owner,
            };
            if (_site.OmitField is { } omit)
                payload.Remove(omit);

            return Json(200, new { data = payload });
        }

        return Json(404, new { exc_type = "NotFound" });
    }

    private static HttpResponseMessage Json(int status, object payload)
    {
        var body = JsonSerializer.Serialize(payload, new JsonSerializerOptions { PropertyNamingPolicy = null });
        return new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}
