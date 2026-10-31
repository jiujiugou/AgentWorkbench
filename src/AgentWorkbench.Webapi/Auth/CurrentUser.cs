using System.Security.Claims;

namespace AgentWorkbench.Webapi.Auth;

/// <summary>从当前请求的身份里取租户与用户。多租户的读写都必须经过这里，禁止从请求体读归属。</summary>
/// <remarks>
/// 为什么归属只信 Claim：此前 <c>ChatRequest.ConversationId</c> / <c>HarnessRunBody.SessionId</c>
/// 都由客户端提供，等于「谁拿到 id 谁就能读」，加多租户后如果还从请求体取归属，
/// 隔离就只是装饰。Claim 由服务端签发（登录时写入 cookie），客户端改不了。
/// </remarks>
public static class CurrentUser
{
    public const string TenantIdClaim = "tenant_id";

    public static string? UserId(this ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string? TenantId(this ClaimsPrincipal user)
        => user.FindFirstValue(TenantIdClaim);

    public static string? DisplayName(this ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.GivenName) ?? user.Identity?.Name;

    /// <summary>取当前租户+用户；未登录或 claim 缺失时抛——调用方应已由授权中间件挡住。</summary>
    public static (string TenantId, string UserId) Owner(this ClaimsPrincipal user)
    {
        var tenantId = user.TenantId();
        var userId = user.UserId();
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException("当前请求缺少租户/用户身份，不能执行归属读写。");
        return (tenantId, userId);
    }
}
