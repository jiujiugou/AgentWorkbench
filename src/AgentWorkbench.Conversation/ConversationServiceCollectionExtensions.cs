namespace AgentWorkbench.Conversation;

/// <summary>
/// Conversation 模块（对话 Agent，基于 Microsoft Agent Framework）：
/// 流式对话 + 结构化输出 + 原生客户端出口。
/// </summary>
public static class ConversationServiceCollectionExtensions
{
    public static IServiceCollection AddNitroConversation(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IChatService, ChatService>();
        services.AddSingleton<IStructuredChatService, StructuredChatService>();
        services.AddSingleton<IChatClientProvider, ChatClientProvider>();
        return services;
    }
}
