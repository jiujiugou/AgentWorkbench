using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentWorkbench.Memory;

/// <summary>Memory 模块（状态与记忆）：会话存储与记忆配置的注册入口。</summary>
public static class MemoryServiceCollectionExtensions
{
    public static IServiceCollection AddNitroMemory(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MemoryOptions>()
            .Bind(configuration.GetSection(MemoryOptions.SectionName));
        // 供 TTL / 淘汰判定使用；测试可注册假时间源覆盖（不覆盖已有注册）。
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISessionStore, SessionStore>();
        return services;
    }
}
