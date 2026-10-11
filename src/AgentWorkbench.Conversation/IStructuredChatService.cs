namespace AgentWorkbench.Conversation;

/// <summary>
/// 结构化输出扩展点：驱动对话模型返回可反序列化的强类型 JSON（供工作流步骤等需要结构化结果的调用方使用）。
/// 与流式 <see cref="IChatService"/> 互补，纯增量。
/// </summary>
public interface IStructuredChatService
{
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    Task<T?> GetJsonAsync<T>(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}
