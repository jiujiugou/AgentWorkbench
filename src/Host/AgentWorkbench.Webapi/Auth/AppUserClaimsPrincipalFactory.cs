using System.Security.Claims;
using AgentWorkbench.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Webapi.Auth;

/// <summary>
/// 在框架生成的 Claims 上补两个业务字段：租户归属与显示名。
/// </summary>
/// <remarks>
/// Identity 默认只给 <c>NameIdentifier</c> / <c>Name</c> / <c>SecurityStamp</c>，
/// 而本仓的归属读写全部依赖 <c>tenant_id</c>（见 <see cref="CurrentUser"/>），
/// 界面显示名也不等于登录名。这两个字段必须由应用补，框架不替业务决定。
/// 角色不用在这里补：<c>UserClaimsPrincipalFactory&lt;TUser,TRole&gt;</c> 已经会写入 Role Claims。
/// </remarks>
public sealed class AppUserClaimsPrincipalFactory : UserClaimsPrincipalFactory<AppUser, IdentityRole>
{
    public AppUserClaimsPrincipalFactory(
        UserManager<AppUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(CurrentUser.TenantIdClaim, user.TenantId));
        identity.AddClaim(new Claim(ClaimTypes.GivenName, user.DisplayName));
        return identity;
    }
}
