namespace AgentWorkbench.Conversation;

/// <summary>
/// Conversation 模块（对话 Agent，基于 Microsoft Agent Framework）：流式对话生成。
/// </summary>
/// <remarks>
/// 阶段 4 砍伐：删掉两个零消费者的扩展点 —— <c>IStructuredChatService</c>（结构化输出，
/// 全仓无调用方）与 <c>IChatClientProvider</c>（原生客户端出口，阶段 1 统一执行路径后已无人走）。
/// 两者都只有「定义 + 注册」两行、没有任何消费者，属死代码。
/// </remarks>
public static class ConversationServiceCollectionExtensions
{
    public static IServiceCollection AddNitroConversation(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IChatService, ChatService>();
        return services;
    }
}
