using AgentWorkbench.Persistence;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Webapi.Auth;

/// <summary>演示租户与账号的种子（仅当库中一个用户都没有、且配置允许时执行）。</summary>
/// <remarks>
/// 为什么要有它：多租户隔离「可被看见」的前提是有两个租户。
/// 没有种子，新克隆跑起来只有一个空登录页，谁也演不了「A 看不到 B」。
/// 生产环境把 <c>Identity:SeedDemo</c> 关掉即可（默认关，仓库内 Development 打开）。
/// 密码固定 <c>demo1234</c>：这是演示账号，不是生产账号——文档与日志里都明说。
/// </remarks>
public static class IdentitySeeder
{
    public const string DemoPassword = "demo1234";

    public static async Task SeedDemoIdentityAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetService<IOptions<IdentityOptions>>()?.Value ?? new IdentityOptions();
        if (!options.SeedDemo)
            return;

        var identity = scope.ServiceProvider.GetRequiredService<IIdentityRepository>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(IdentitySeeder));

        if (await identity.CountUsersAsync() > 0)
            return;

        var acme = await identity.CreateTenantAsync("华北智造");
        var globex = await identity.CreateTenantAsync("华东自动化");

        await identity.CreateUserAsync(acme.Id, "wang", "王工", PasswordHasher.Hash(DemoPassword), "owner");
        await identity.CreateUserAsync(acme.Id, "zhao", "赵工", PasswordHasher.Hash(DemoPassword), "member");
        await identity.CreateUserAsync(globex.Id, "li", "李工", PasswordHasher.Hash(DemoPassword), "owner");

        logger.LogInformation(
            "已种子演示租户：华北智造（wang/zhao）、华东自动化（li），统一密码 {Password}",
            DemoPassword);
    }
}

/// <summary>身份相关配置（<c>Identity</c> 配置节）。</summary>
public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    /// <summary>库中无用户时是否种子演示租户与账号。默认关，避免生产环境凭空多出弱口令账号。</summary>
    public bool SeedDemo { get; set; }
}
