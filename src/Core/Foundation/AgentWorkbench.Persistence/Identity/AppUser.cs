using Microsoft.AspNetCore.Identity;

namespace AgentWorkbench.Persistence;

/// <summary>
/// ASP.NET Core Identity 的用户主体。框架负责用户名规范化、密码哈希、锁定、并发戳；
/// 本类只补两个业务侧字段：租户归属与显示名。
/// </summary>
/// <remarks>
/// 密码哈希由框架 <c>PasswordHasher&lt;AppUser&gt;</c>（PBKDF2-HMAC-SHA512, v3 格式）负责，
/// 仓内不再有任何自写哈希实现。<c>Role</c> 也不再是字符串列，而是 Identity 角色表。
/// </remarks>
public sealed class AppUser : IdentityUser
{
    /// <summary>所属租户。<c>tenants.id</c>。</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>界面显示名（工号/姓名），与登录名分开。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>停用标记。Identity 的锁定（Lockout）管的是失败次数，这个管的是行政停用。</summary>
    public bool IsActive { get; set; } = true;

    public string CreatedAt { get; set; } = string.Empty;
}
