using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWorkbench.Memory;

/// <summary>Memory 模块（状态与记忆）：会话存储与记忆配置的注册入口。</summary>
public static class MemoryServiceCollectionExtensions
{
    public static IServiceCollection AddNitroMemory(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MemoryOptions>()
            .Bind(configuration.GetSection(MemoryOptions.SectionName));
        services.AddSingleton<ISessionStore, SessionStore>();
        return services;
    }
}
