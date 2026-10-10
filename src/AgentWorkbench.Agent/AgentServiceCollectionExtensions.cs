namespace AgentWorkbench.Agent;

/// <summary>Agent 模块（Microsoft Agent Framework）：对话 Agent 运行时 + 工具扩展点。</summary>
public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddNitroAgents(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<IStructuredChatService, StructuredChatService>();
        services.AddSingleton<IChatClientProvider, ChatClientProvider>();
        return services;
    }
}
