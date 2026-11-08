namespace AgentWorkbench.Erp.ErpNext;

using System.Globalization;
using AgentWorkbench.Ports;

/// <summary>
/// M2 只读适配器（M2 设计 §3 / §6 / §7）。
///
/// 只实现 Query：
/// - Query 之外的三个原语一律 <c>not_supported_in_m2</c> 且**零 HTTP 调用**。
///   M2 里比「不能写」更糟的是「以为写了，其实写进了内存假 ERP」（设计 §3.2）。
/// - 配置缺失时直接拒绝，**不回落**任何假实现。
///
/// 失败语义按 §6 的唯一映射表逐条落：查不到（404）与出错（5xx）必须分开，
/// 否则「查不到时明确说查不到」这句判据就是假的。
/// </summary>
public sealed class ErpNextQueryAdapter : IActionExecutor
{
    private static readonly string[] RequiredFields = ["name", "docstatus", "status", "company", "grand_total", "currency", "modified"];

    private readonly IErpTransport _transport;
    private readonly ErpNextOptions _options;

    public ErpNextQueryAdapter(IErpTransport transport, ErpNextOptions options)
    {
        _transport = transport;
        _options = options;
    }

    public async ValueTask<ActionResult> ExecuteAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
    {
        // 写原语在 M2 明确拒绝：注册了 spec 但没有实现，比「工具不存在」更容易排查。
        if (!string.Equals(spec.Primitive, ActionPrimitives.Query, StringComparison.Ordinal))
            return Fail(request.RequestId, ErpErrorCodes.NotSupportedInM2, $"M2 只读：{spec.Primitive} 不接真 ERP");

        if (!_options.IsConfigured)
            return Reject(request.RequestId, ErpErrorCodes.ConfigurationMissing,
                "缺少配置：" + string.Join(", ", _options.MissingKeys()));

        if (!TryReadDocumentId(request, out var documentId))
            return Fail(request.RequestId, ErpErrorCodes.InvalidArguments, "参数缺 document_id");

        var path = $"/api/resource/{Uri.EscapeDataString(_options.PurchaseOrderDoctype)}/{Uri.EscapeDataString(documentId)}";
        var response = await _transport.GetAsync(path, query: null, ct);

        if (response.TimedOut)
            return Unknown(request.RequestId, ErpErrorCodes.Timeout, "ERP 响应超时，状态未知");

        if (response.TransportError is not null)
            return Fail(request.RequestId, ErpErrorCodes.UpstreamError, response.TransportError);

        switch (response.StatusCode)
        {
            case 401:
                return Fail(request.RequestId, ErpErrorCodes.AuthFailed, "集成凭据失效");
            case 403:
                return Fail(request.RequestId, ErpErrorCodes.Forbidden, "集成账号无权读取该单据");
            case 404:
                return Fail(request.RequestId, ErpErrorCodes.NotFound, "ERP 里查不到这张单");
            case 429:
                return Fail(request.RequestId, ErpErrorCodes.RateLimited, "ERP 限流");
            case >= 500:
                return Fail(request.RequestId, ErpErrorCodes.UpstreamError, $"ERP 上游 {response.StatusCode}");
        }

        if (!response.Ok)
            return Fail(request.RequestId, ErpErrorCodes.UpstreamError, $"ERP 返回 {response.StatusCode}");

        return Parse(request.RequestId, response.Body, path);
    }

    /// <summary>解析 200 响应。字段缺失不猜默认值，直接 schema_mismatch（§7）。</summary>
    private ActionResult Parse(string requestId, string body, string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Fail(requestId, ErpErrorCodes.ErrorPayload, "响应不是合法 JSON");
        }

        using (document)
        {
            var root = document.RootElement;

            // Frappe 的错误有时包在 200 里：exc / exc_type / _server_messages 任一出现即为错误载荷。
            if (root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("exc", out _) || root.TryGetProperty("exc_type", out _) ||
                 root.TryGetProperty("exception", out _) || root.TryGetProperty("_server_messages", out _)))
                return Fail(requestId, ErpErrorCodes.ErrorPayload, ReadErrorMessage(root));

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return Fail(requestId, ErpErrorCodes.SchemaMismatch, $"响应缺 data 对象（{path}）");

            foreach (var field in RequiredFields)
            {
                if (!data.TryGetProperty(field, out _))
                    return Fail(requestId, ErpErrorCodes.SchemaMismatch, $"响应缺字段 {field}");
            }

            var docStatus = ReadDocStatus(data);
            if (docStatus is null)
                return Fail(requestId, ErpErrorCodes.SchemaMismatch, "docstatus 不是整数");

            var modifiedRaw = data.GetProperty("modified").GetString();
            if (string.IsNullOrWhiteSpace(modifiedRaw))
                return Fail(requestId, ErpErrorCodes.SchemaMismatch, "modified 为空");

            if (!TryConvertToOffset(modifiedRaw, out var modifiedAt))
                return Fail(requestId, ErpErrorCodes.SchemaMismatch,
                    $"modified 无法解析为时间（原始串 {modifiedRaw}）");

            var snapshot = new ErpDocumentSnapshot(
                Name: data.GetProperty("name").GetString() ?? string.Empty,
                DocStatus: docStatus.Value,
                Status: data.GetProperty("status").GetString() ?? string.Empty,
                Company: data.GetProperty("company").GetString() ?? string.Empty,
                Supplier: ReadString(data, "supplier"),
                GrandTotal: ReadDecimal(data, "grand_total"),
                Currency: ReadString(data, "currency"),
                ModifiedRaw: modifiedRaw,
                ModifiedAt: modifiedAt,
                TimeZoneId: _options.TimeZoneId!,
                Doctype: _options.PurchaseOrderDoctype);

            return new ActionResult(
                requestId,
                ActionStatuses.Succeeded,
                "erp_query_ok",
                snapshot.Name,
                null,
                JsonSerializer.SerializeToElement(snapshot.ToData()));
        }
    }

    /// <summary>
    /// ERPNext 给的是站点时区的 naive 串（官方示例 2019-06-03 14:19:00.281026）。
    /// 必须按站点时区补偏移；猜本机时区会让「什么时候变的」这句话直接错。
    /// </summary>
    private bool TryConvertToOffset(string raw, out DateTimeOffset value)
    {
        value = default;
        var formats = new[] { "yyyy-MM-dd HH:mm:ss.ffffff", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fffffff" };
        if (!DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZoneId!);
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }

        value = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz.GetUtcOffset(local));
        return true;
    }

    private static int? ReadDocStatus(JsonElement data)
    {
        if (!data.TryGetProperty("docstatus", out var node))
            return null;
        return node.ValueKind switch
        {
            JsonValueKind.Number when node.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(node.GetString(), out var n) => n,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement data, string name)
        => data.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;

    private static decimal? ReadDecimal(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var node))
            return null;
        return node.ValueKind switch
        {
            JsonValueKind.Number when node.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(node.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static bool TryReadDocumentId(ActionRequest request, out string documentId)
    {
        documentId = string.Empty;
        if (request.Arguments.ValueKind != JsonValueKind.Object)
            return false;
        if (!request.Arguments.TryGetProperty("document_id", out var node) || node.ValueKind != JsonValueKind.String)
            return false;
        documentId = node.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(documentId);
    }

    private static string ReadErrorMessage(JsonElement root)
    {
        foreach (var key in new[] { "exc_type", "exception", "_server_messages", "exc" })
            if (root.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.String)
                return Truncate(node.GetString() ?? string.Empty);
        return "ERP 返回错误载荷";
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];

    private static ActionResult Fail(string requestId, string code, string reason)
        => new(requestId, ActionStatuses.Failed, code, null, code,
            JsonSerializer.SerializeToElement(new { reason }));

    private static ActionResult Unknown(string requestId, string code, string reason)
        => new(requestId, ActionStatuses.Unknown, code, null, code,
            JsonSerializer.SerializeToElement(new { reason }));

    private static ActionResult Reject(string requestId, string code, string reason)
        => new(requestId, ActionStatuses.Rejected, code, null, code,
            JsonSerializer.SerializeToElement(new { reason }));
}
