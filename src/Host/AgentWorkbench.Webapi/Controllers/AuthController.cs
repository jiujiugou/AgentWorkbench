using AgentWorkbench.Persistence;
using AgentWorkbench.Webapi.Problems;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

public sealed record LoginRequest(string UserName, string Password);

/// <summary>登录 / 登出 / 当前身份。全部走 ASP.NET Core Identity，不再手写查库验密。</summary>
/// <remarks>
/// <para>用 Cookie 而不是 Bearer：前端与后端同源（vite 代理 /api 与 /hubs），cookie 天然随请求走，
/// SignalR 握手也带得上；省掉一套 token 刷新逻辑。</para>
/// <para>登录由 <see cref="SignInManager{TUser}.PasswordSignInAsync"/> 完成：用户名规范化、密码哈希校验、
/// 失败计数与锁定、Cookie 签发都在框架内。控制器只负责「把结果翻成 HTTP 语义」。</para>
/// </remarks>
[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly SignInManager<AppUser> _signIn;
    private readonly UserManager<AppUser> _users;
    private readonly ITenantRepository _tenants;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        SignInManager<AppUser> signIn,
        UserManager<AppUser> users,
        ITenantRepository tenants,
        ILogger<AuthController> logger)
    {
        _signIn = signIn;
        _users = users;
        _tenants = tenants;
        _logger = logger;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.UserName) || string.IsNullOrEmpty(body.Password))
            return this.Result(new ApiError(401, null, "用户名和密码都不能为空。"));

        var userName = body.UserName.Trim();
        var signIn = await _signIn.PasswordSignInAsync(userName, body.Password, isPersistent: true, lockoutOnFailure: true);

        // 锁定与「密码错」必须给不同提示，否则用户不知道是等一会儿还是找管理员。
        if (signIn.IsLockedOut)
        {
            _logger.LogWarning("登录被拒（账号锁定）：{UserName}", userName);
            return this.Result(new ApiError(423, null, "账号因多次登录失败已临时锁定，请稍后再试。"));
        }

        if (!signIn.Succeeded)
        {
            _logger.LogWarning("登录失败：{UserName}", userName);
            return this.Result(new ApiError(401, null, "用户名或密码不正确。"));
        }

        var user = await _users.FindByNameAsync(userName);
        if (user is null)
            return this.Result(new ApiError(401, null, "用户名或密码不正确。"));

        // 行政停用与 Identity 的锁定是两件事：密码对了也要挡回去，并撤销刚签发的 cookie。
        if (!user.IsActive)
        {
            await _signIn.SignOutAsync();
            return this.Result(new ApiError(401, null, "账号已停用。"));
        }

        var tenant = await _tenants.GetTenantAsync(user.TenantId);
        _logger.LogInformation("登录成功：{UserName}（租户 {TenantId}）", user.UserName, user.TenantId);
        return Ok(await ToViewAsync(user, tenant?.Name));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return NoContent();
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var user = await _users.GetUserAsync(User);
        if (user is null)
            return Unauthorized();

        var tenant = await _tenants.GetTenantAsync(user.TenantId);
        return Ok(await ToViewAsync(user, tenant?.Name));
    }

    private async Task<object> ToViewAsync(AppUser user, string? tenantName)
    {
        var roles = await _users.GetRolesAsync(user);
        return new
        {
            userId = user.Id,
            userName = user.UserName,
            displayName = user.DisplayName,
            // 前端只读一个角色字段；多角色时取主角色。
            role = roles.FirstOrDefault() ?? "member",
            tenantId = user.TenantId,
            tenantName
        };
    }
}
