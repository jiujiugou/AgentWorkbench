using AgentWorkbench.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Webapi.Auth;

/// <summary>演示租户与账号的种子（仅当库中一个用户都没有、且配置允许时执行）。</summary>
/// <remarks>
/// 账号创建与密码哈希全部走 <see cref="UserManager{TUser}"/>：框架负责用户名规范化、
/// PBKDF2-HMAC-SHA512 哈希、安全戳与并发戳，仓内不再有任何自写哈希实现。
/// 角色也不再是字符串列，而是 Identity 角色表 + 用户角色关联。
/// 生产环境把 <c>Identity:SeedDemo</c> 关掉即可（默认关，仓库内 Development 打开）。
/// </remarks>
public static class IdentitySeeder
{
    /// <summary>演示账号统一密码。含大写与数字，满足框架默认密码策略（不为了兼容旧习惯去放宽策略）。</summary>
    public const string DemoPassword = "Demo1234";

    private static readonly string[] Roles = ["owner", "member"];

    public static async Task SeedDemoIdentityAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetService<IOptions<DemoIdentityOptions>>()?.Value ?? new DemoIdentityOptions();
        if (!options.SeedDemo)
            return;

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentitySeeder));

        if (await users.Users.AnyAsync(cancellationToken))
            return;

        foreach (var role in Roles)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                var created = await roles.CreateAsync(new IdentityRole(role));
                if (!created.Succeeded)
                    throw new InvalidOperationException(
                        $"种子角色 {role} 创建失败：{string.Join("; ", created.Errors.Select(e => e.Description))}");
            }
        }

        // 租户可能已由更早的演示数据建过（tenants.name 唯一），存在就复用，不要重复插。
        var existingTenants = await tenants.ListTenantsAsync();
        var acme = existingTenants.FirstOrDefault(t => t.Name == "华北智造")
                   ?? await tenants.CreateTenantAsync("华北智造");
        var globex = existingTenants.FirstOrDefault(t => t.Name == "华东自动化")
                     ?? await tenants.CreateTenantAsync("华东自动化");

        await CreateUserAsync(users, acme.Id, "wang", "王工", "owner");
        await CreateUserAsync(users, acme.Id, "zhao", "赵工", "member");
        await CreateUserAsync(users, globex.Id, "li", "李工", "owner");

        logger.LogInformation(
            "已种子演示租户：华北智造（wang/zhao）、华东自动化（li），统一密码 {Password}",
            DemoPassword);
    }

    private static async Task CreateUserAsync(
        UserManager<AppUser> users,
        string tenantId,
        string userName,
        string displayName,
        string role)
    {
        var user = new AppUser
        {
            UserName = userName,
            DisplayName = displayName,
            TenantId = tenantId,
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };

        var created = await users.CreateAsync(user, DemoPassword);
        if (!created.Succeeded)
            throw new InvalidOperationException(
                $"种子用户 {userName} 创建失败：{string.Join("; ", created.Errors.Select(e => e.Description))}");

        var added = await users.AddToRoleAsync(user, role);
        if (!added.Succeeded)
            throw new InvalidOperationException(
                $"种子用户 {userName} 赋角色失败：{string.Join("; ", added.Errors.Select(e => e.Description))}");
    }
}

/// <summary>演示种子配置（<c>Identity</c> 配置节）。</summary>
/// <remarks>
/// 刻意不叫 <c>IdentityOptions</c>：那是框架的类型，同名会让读者以为这里在配框架选项。
/// 框架的密码/锁定策略在 <c>Program.cs</c> 的 <c>AddIdentityCore</c> 里配，不走本类。
/// </remarks>
public sealed class DemoIdentityOptions
{
    public const string SectionName = "Identity";

    /// <summary>库中无用户时是否种子演示租户与账号。默认关，避免生产环境凭空多出弱口令账号。</summary>
    public bool SeedDemo { get; set; }
}
