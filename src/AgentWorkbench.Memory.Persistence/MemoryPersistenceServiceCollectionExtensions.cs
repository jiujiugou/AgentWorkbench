using AgentWorkbench.Memory.Persistence.Tools;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentWorkbench.Memory.Persistence;

/// <summary>
/// Memory.Persistence 模块（状态与记忆的落库实现）：把 <c>AddNitroMemory</c> 注册的内存版
/// <see cref="ISessionStore"/> 换成 SQLite 版，注册长期记忆载体与记忆工具，并启用过期清理。
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

        services.AddSingleton<IMemoryItemRepository, SqliteMemoryItemRepository>();
        services.Replace(ServiceDescriptor.Singleton<ILongTermMemoryStore, SqliteLongTermMemoryStore>());

        // 关掉长期记忆时干脆不注册工具：模型看不到 = 调不动，比注册一个只会回「未启用」的工具更干净。
        if (LongTermEnabled(configuration))
        {
            services.AddSingleton<IAgentToolProvider, RememberTool>();
            services.AddSingleton<IAgentToolProvider, RecallTool>();
        }

        services.AddHostedService<MemoryRetentionService>();
        return services;
    }

    private static bool LongTermEnabled(IConfiguration configuration)
        => configuration.GetSection(MemoryOptions.SectionName).Get<MemoryOptions>()?.LongTermEnabled ?? true;
}
