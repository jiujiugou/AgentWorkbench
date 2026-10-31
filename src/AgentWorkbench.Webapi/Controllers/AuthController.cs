using System.Security.Claims;
using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using AgentWorkbench.Webapi.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

public sealed record LoginRequest(string UserName, string Password);

/// <summary>登录 / 登出 / 当前身份。</summary>
/// <remarks>
/// 用 Cookie 而不是 Bearer：前端与后端同源（vite 代理 /api 与 /hubs），cookie 天然随请求走，
/// SignalR 握手也带得上；省掉一套 token 刷新逻辑（那套在单人演示里纯属负债）。
/// </remarks>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IIdentityRepository _identity;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IIdentityRepository identity, ILogger<AuthController> logger)
    {
        _identity = identity;
        _logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.UserName) || string.IsNullOrEmpty(body.Password))
            return Unauthorized(new { message = "用户名和密码都不能为空。" });

        var user = await _identity.FindUserAsync(body.UserName.Trim());
        // 用户不存在与密码错误返回同一句话：不泄露「这个账号存在」。
        if (user is null || !PasswordHasher.Verify(body.Password, user.PasswordHash))
        {
            _logger.LogWarning("登录失败：{UserName}", body.UserName);
            return Unauthorized(new { message = "用户名或密码不正确。" });
        }

        if (!user.IsActive)
            return Unauthorized(new { message = "账号已停用。" });

        var tenant = await _identity.GetTenantAsync(user.TenantId);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim(ClaimTypes.GivenName, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim(CurrentUser.TenantIdClaim, user.TenantId)
        ], CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7) });

        _logger.LogInformation("登录成功：{UserName}（租户 {TenantId}）", user.UserName, user.TenantId);
        return Ok(ToView(user, tenant?.Name));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var userId = User.UserId();
        if (userId is null)
            return Unauthorized();
        var user = await _identity.GetUserAsync(userId);
        if (user is null)
            return Unauthorized();
        var tenant = await _identity.GetTenantAsync(user.TenantId);
        return Ok(ToView(user, tenant?.Name));
    }

    private static object ToView(UserRecord user, string? tenantName) => new
    {
        userId = user.Id,
        userName = user.UserName,
        displayName = user.DisplayName,
        role = user.Role,
        tenantId = user.TenantId,
        tenantName
    };
}
