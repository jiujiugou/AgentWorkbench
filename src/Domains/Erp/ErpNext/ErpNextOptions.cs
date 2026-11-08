namespace AgentWorkbench.Erp.ErpNext;

/// <summary>
/// 真接 ERPNext 的配置（M2 设计 §3）。
/// 用 plain record 而不是 IOptions，是为了让本工程只依赖 BCL + Core.Ports —— 动作域不引 DI / Options 包。
/// 由组合根从 IConfiguration 构造后传入。
/// </summary>
public sealed record ErpNextOptions(
    string? BaseUrl = null,
    string? ApiKey = null,
    string? ApiSecret = null,
    string? TimeZoneId = null,
    string PurchaseOrderDoctype = "Purchase Order",
    TimeSpan? Timeout = null,
    int MaxReadRetries = 2)
{
    /// <summary>缺任何一项都不接真域，直接 fail-closed，**不回落假 ERP**（M2 设计 §3.1 决定 3）。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ApiSecret)
        && !string.IsNullOrWhiteSpace(TimeZoneId);

    public TimeSpan EffectiveTimeout => Timeout ?? TimeSpan.FromSeconds(20);

    /// <summary>缺哪些项，用于拒绝原因（不打印值，只打印项名）。</summary>
    public IReadOnlyList<string> MissingKeys()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(BaseUrl)) missing.Add("BaseUrl");
        if (string.IsNullOrWhiteSpace(ApiKey)) missing.Add("ApiKey");
        if (string.IsNullOrWhiteSpace(ApiSecret)) missing.Add("ApiSecret");
        if (string.IsNullOrWhiteSpace(TimeZoneId)) missing.Add("TimeZoneId");
        return missing;
    }
}
