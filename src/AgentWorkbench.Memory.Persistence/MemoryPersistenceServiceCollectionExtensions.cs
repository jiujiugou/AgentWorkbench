using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentWorkbench.Memory.Persistence;

/// <summary>
/// Memory.Persistence 模块（状态与记忆的落库实现）：把 <c>AddNitroMemory</c> 注册的内存版
/// <see cref="ISessionStore"/> 换成 SQLite 版，并启用过期清理。
/// </summary>
public static class MemoryPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddNitroMemoryPersistence(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MemoryOptions>()
            .Bind(configuration.GetSection(MemoryOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<ISessionStateRepository, SqliteSessionStateRepository>();
        services.Replace(ServiceDescriptor.Singleton<ISessionStore, SqliteSessionStore>());
        services.AddHostedService<SessionRetentionService>();
        return services;
    }
}
