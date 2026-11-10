using AgentWorkbench.Persistence;
using AgentWorkbench.Webapi.Auth;
using AgentWorkbench.Webapi.Problems;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AgentWorkbench.Webapi.Controllers;

public sealed record CreateMemberBody(
    string? UserName,
    string? DisplayName,
    string? Email,
    string? Password,
    string? Role);

public sealed record UpdateMemberBody(string? DisplayName, string? Role);

/// <summary>
/// 成员管理：直接建号，不走邀请。
/// </summary>
/// <remarks>
/// <para>
/// v1 的形态是「管理员发邀请链接 → 对方点链接自己设密码」。私有化部署的实际用法不是这样：
/// 账号开在内网、由管理员当场交付，没有邮件通道，邀请链接还得有人转发。
/// 2026-11-10 起改为直接建号 + 初始口令，管理员当面告知，首次登录后自行修改。
/// </para>
/// <para>
/// <b>租户边界</b>：归属只从 Claim 取（<see cref="CurrentUser.Owner"/>），请求体里不接受 tenantId。
/// 列表与写入都按当前租户过滤——同一个 <c>/api/members/{id}</c> 打别人的 id 返回 404，
/// 而不是 403：403 等于确认「这个 id 存在，只是不属于你」。
/// </para>
/// <para>
/// <b>角色不改根</b>：owner 是部署者（种子账号），接口不提供创建或改写 owner 的能力；
/// 目标是 owner 的行一律拒绝，避免管理员把自己的上级降权。
/// </para>
/// <para>
/// <b>停用不等于立即踢线</b>：停用写 <see cref="AppUser.IsActive"/>，登录会被
/// <c>AuthController.Login</c> 挡回；但已经签发的 Cookie 仍有效，直到 Cookie 校验
/// 重新读库（Identity 的 SecurityStamp 校验间隔，默认 30 分钟）。文案按这个事实写，
/// 不要承诺「立即失效」。
/// </para>
/// </remarks>
[ApiController]
[Route("api/members")]
[Authorize(Roles = "owner,admin")]
public sealed class MembersController : ControllerBase
{
    /// <summary>允许通过接口授予的角色。owner 刻意不在其中。</summary>
    private static readonly string[] AssignableRoles = ["admin", "member"];

    private readonly UserManager<AppUser> _users;
    private readonly ILogger<MembersController> _logger;

    public MembersController(UserManager<AppUser> users, ILogger<MembersController> logger)
    {
        _users = users;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var (tenantId, _) = User.Owner();
        var users = await _users.Users
            .Where(u => u.TenantId == tenantId)
            .OrderBy(u => u.UserName)
            .ToListAsync();

        var result = new List<object>(users.Count);
        foreach (var user in users)
            result.Add(await ToViewAsync(user));

        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMemberBody body)
    {
        var (tenantId, _) = User.Owner();

        var userName = body.UserName?.Trim();
        if (string.IsNullOrWhiteSpace(userName))
            return this.Result(new ApiError(400, "user_name_required", "登录名不能为空。"));

        var role = string.IsNullOrWhiteSpace(body.Role) ? "member" : body.Role.Trim();
        if (!AssignableRoles.Contains(role, StringComparer.Ordinal))
            return this.Result(new ApiError(400, "role_invalid", "角色只能是 admin 或 member。"));

        if (string.IsNullOrEmpty(body.Password))
            return this.Result(new ApiError(400, "password_required", "初始口令不能为空。"));

        // 先查重再建：CreateAsync 也会查，但它的报错混在 IdentityResult 里，
        // 翻成人话要走一层映射；这里直接给出「登录名已存在」更好定位。
        var existing = await _users.FindByNameAsync(userName);
        if (existing is not null)
            return this.Result(new ApiError(409, "user_name_taken", $"登录名「{userName}」已存在。"));

        var user = new AppUser
        {
            UserName = userName,
            Email = string.IsNullOrWhiteSpace(body.Email) ? null : body.Email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? userName : body.DisplayName.Trim(),
            TenantId = tenantId,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        var created = await _users.CreateAsync(user, body.Password);
        if (!created.Succeeded)
        {
            _logger.LogWarning("建号失败：{UserName} {Errors}", userName, string.Join(";", created.Errors.Select(e => e.Code)));
            return this.Result(new ApiError(400, "create_failed", Describe(created)));
        }

        var added = await _users.AddToRoleAsync(user, role);
        if (!added.Succeeded)
        {
            // 角色没加上就是一个半成品账号：能登录但没有任何角色，界面会当成 member。
            // 直接回滚，别让它留在库里等人发现。
            await _users.DeleteAsync(user);
            _logger.LogError("加角色失败并已回滚：{UserName} {Errors}", userName, string.Join(";", added.Errors.Select(e => e.Code)));
            return this.Result(new ApiError(500, "role_assign_failed", Describe(added)));
        }

        _logger.LogInformation("已建号：{UserName} 角色 {Role} 租户 {TenantId}", userName, role, tenantId);
        return Ok(await ToViewAsync(user));
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateMemberBody body)
    {
        var target = await FindInTenantAsync(id);
        if (target is null) return NotFound();

        if (IsOwner(await _users.GetRolesAsync(target)))
            return this.Result(new ApiError(403, "owner_immutable", "部署者账号不能通过接口修改。"));

        if (!string.IsNullOrWhiteSpace(body.DisplayName))
        {
            target.DisplayName = body.DisplayName.Trim();
            var renamed = await _users.UpdateAsync(target);
            if (!renamed.Succeeded)
                return this.Result(new ApiError(400, "update_failed", Describe(renamed)));
        }

        if (!string.IsNullOrWhiteSpace(body.Role))
        {
            var role = body.Role.Trim();
            if (!AssignableRoles.Contains(role, StringComparer.Ordinal))
                return this.Result(new ApiError(400, "role_invalid", "角色只能是 admin 或 member。"));

            var current = await _users.GetRolesAsync(target);
            if (!current.Contains(role, StringComparer.Ordinal))
            {
                // 先摘旧角色再挂新的：Identity 不禁止多角色，两处都留着会让
                // AuthController 的「取主角色」变成一个不确定的 FirstOrDefault。
                var removed = await _users.RemoveFromRolesAsync(target, current);
                if (!removed.Succeeded)
                    return this.Result(new ApiError(500, "role_change_failed", Describe(removed)));

                var added = await _users.AddToRoleAsync(target, role);
                if (!added.Succeeded)
                    return this.Result(new ApiError(500, "role_assign_failed", Describe(added)));

                // 角色变了，已签发的 Cookie 里的角色 Claim 还是旧的。换 stamp 让它下一轮失效。
                await _users.UpdateSecurityStampAsync(target);
            }
        }

        return Ok(await ToViewAsync(target));
    }

    [HttpPost("{id}/disable")]
    public Task<IActionResult> Disable(string id) => SetActiveAsync(id, false);

    [HttpPost("{id}/enable")]
    public Task<IActionResult> Enable(string id) => SetActiveAsync(id, true);

    private async Task<IActionResult> SetActiveAsync(string id, bool active)
    {
        var target = await FindInTenantAsync(id);
        if (target is null) return NotFound();

        if (IsOwner(await _users.GetRolesAsync(target)))
            return this.Result(new ApiError(403, "owner_immutable", "部署者账号不能停用。"));

        if (target.IsActive == active) return NoContent();

        target.IsActive = active;
        var updated = await _users.UpdateAsync(target);
        if (!updated.Succeeded)
            return this.Result(new ApiError(400, "update_failed", Describe(updated)));

        // 停用要尽快作用于已登录会话：换 stamp 后 Cookie 校验会判失效。
        // 实际生效时间取决于 SecurityStampValidator 的验证间隔，不做「立即」承诺。
        if (!active) await _users.UpdateSecurityStampAsync(target);

        _logger.LogInformation("账号{Action}：{UserName}", active ? "恢复" : "停用", target.UserName);
        return NoContent();
    }

    /// <summary>按 id 在当前租户内找；找不到与不属于本租户一律返回 null（对外都是 404）。</summary>
    private async Task<AppUser?> FindInTenantAsync(string id)
    {
        var (tenantId, _) = User.Owner();
        var user = await _users.FindByIdAsync(id);
        return user is not null && string.Equals(user.TenantId, tenantId, StringComparison.Ordinal) ? user : null;
    }

    private static bool IsOwner(IList<string> roles) => roles.Contains("owner", StringComparer.Ordinal);

    private async Task<object> ToViewAsync(AppUser user)
    {
        var roles = await _users.GetRolesAsync(user);
        return new
        {
            id = user.Id,
            userName = user.UserName,
            displayName = user.DisplayName,
            email = user.Email,
            role = roles.FirstOrDefault() ?? "member",
            status = user.IsActive ? "active" : "disabled",
            tenantId = user.TenantId,
            createdAt = user.CreatedAt,
        };
    }

    /// <summary>把 IdentityResult 翻成一句能给人看的话，不把 ErrorCode 直接抛给界面。</summary>
    private static string Describe(IdentityResult result)
    {
        if (result.Succeeded) return string.Empty;
        var parts = result.Errors
            .Where(e => !string.IsNullOrWhiteSpace(e.Description))
            .Select(e => e.Description)
            .Distinct()
            .ToArray();
        return parts.Length > 0 ? string.Join("；", parts) : "操作未完成。";
    }
}
