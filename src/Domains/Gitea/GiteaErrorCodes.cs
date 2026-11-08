namespace AgentWorkbench.Gitea;

/// <summary>
/// 错误码按 2026-11-08 对 gitea.com 的实测映射，见
/// docs/验证与现状/gitea-真实行为-20261108.md。
/// 这些字符串是动作域语义，底座只透传 ActionResult.ErrorCode，不参与决策。
/// </summary>
public static class GiteaErrorCodes
{
    /// <summary>配置缺失：零 HTTP 直接拒绝（fail-closed）。</summary>
    public const string NotConfigured = "gitea_not_configured";

    /// <summary>401：无 token 或 token 无效。不可重试。</summary>
    public const string NotAuthenticated = "not_authenticated";

    /// <summary>403：token 有效但 scope 不足。不可重试。</summary>
    public const string ScopeDenied = "scope_denied";

    /// <summary>
    /// 404：不存在，<b>或存在但无权看到其存在</b>。
    /// 实测私有仓库与不存在仓库返回的 body 字节级相同，无法区分，故合并为一个码。
    /// </summary>
    public const string NotFoundOrForbidden = "not_found_or_forbidden";

    public const string InvalidPayload = "invalid_payload";
    public const string UpstreamError = "upstream_error";
    public const string RateLimited = "rate_limited";
    public const string Timeout = "timeout";

    /// <summary>M2 只读边界：写能力在目录里可见，但执行被显式拒绝且零 HTTP。</summary>
    public const string WritesDisabled = "not_supported_in_m2";
}

/// <summary>成功码。同样是动作域语义。</summary>
public static class GiteaSuccessCodes
{
    public const string Read = "gitea_read";
    public const string Created = "gitea_created";
    public const string Closed = "gitea_closed";
}
