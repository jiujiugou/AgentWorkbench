namespace AgentWorkbench.Agent;

/// <summary>
/// 结构化输出扩展点：驱动对话模型返回可反序列化的强类型 JSON（用于要素抽取等工作流）。
/// 与流式 <see cref="IChatService"/> 互补，纯增量。
/// </summary>
public interface IStructuredChatService
{
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    Task<T?> GetJsonAsync<T>(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}
