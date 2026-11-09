using AgentWorkbench.Memory.Persistence.ChatHistory;
using AgentWorkbench.Memory.Persistence.Tools;
using AgentWorkbench.Tools.Abstractions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

        // 对话历史改为逐条追加落库：替换 Context 模块注册的内存版 provider。
        // 换掉之后 agent_sessions 不再承载历史（快照只剩非历史状态），
        // 「整份覆盖」造成的 lost update 随之失去伤害面。
        services.AddSingleton<IChatMessageStore, SqliteChatMessageStore>();
        services.Replace(ServiceDescriptor.Singleton<ChatHistoryProvider>(sp =>
            new SqliteChatHistoryProvider(
                sp.GetRequiredService<IChatMessageStore>(),
                sp.GetRequiredService<ILogger<SqliteChatHistoryProvider>>())));

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
