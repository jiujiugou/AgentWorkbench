namespace AgentWorkbench.Erp.ErpNext;

using AgentWorkbench.Ports;

/// <summary>
/// M2 的失败码（M2 设计 §6 唯一映射表）。区分「查不到」与「出错」是判据 1 的字面要求。
/// 这些是动作域码，不是底座码：底座只认 ActionStatuses，不认识 ERP 的失败原因。
/// </summary>
public static class ErpErrorCodes
{
    public const string NotFound = "erp_not_found";
    public const string Forbidden = "erp_forbidden";
    public const string AuthFailed = "erp_auth_failed";
    public const string UpstreamError = "erp_upstream_error";
    public const string RateLimited = "erp_rate_limited";
    public const string Timeout = "erp_timeout";
    public const string ErrorPayload = "erp_error_payload";
    public const string SchemaMismatch = "erp_schema_mismatch";
    public const string ConfigurationMissing = "configuration_missing";
    public const string NotSupportedInM2 = "not_supported_in_m2";
    public const string InvalidArguments = "invalid_arguments";
}

/// <summary>ERPNext 单据快照：只保留 M2 认领过的字段（M2 设计 §7），不做通用映射。</summary>
public sealed record ErpDocumentSnapshot(
    string Name,
    int DocStatus,
    string Status,
    string Company,
    string? Supplier,
    decimal? GrandTotal,
    string? Currency,
    string? ModifiedRaw,
    DateTimeOffset? ModifiedAt,
    string TimeZoneId,
    string Doctype)
{
    /// <summary>docstatus 是 Frappe 定义的权威提交态；status 字符串随版本变，只当 opaque 展示用。</summary>
    public string DocStatusText => DocStatus switch
    {
        0 => "draft",
        1 => "submitted",
        2 => "cancelled",
        _ => "unknown",
    };

    public object ToData() => new
    {
        external_id = Name,
        docstatus = DocStatus,
        docstatus_text = DocStatusText,
        status = Status,
        company = Company,
        supplier = Supplier,
        grand_total = GrandTotal,
        currency = Currency,
        // 时间必须三个都给：原始串、带偏移的时间、以及用的哪个时区。
        // ERPNext 返回的是站点时区的 naive 串，直接透传等于没给时间（M2 设计 §7.1）。
        erp_modified_raw = ModifiedRaw,
        erp_modified_at = ModifiedAt?.ToString("O"),
        erp_time_zone = TimeZoneId,
        doctype = Doctype,
    };
}
