namespace AgentWorkbench.Conversation;

/// <summary>对话生成服务：基于 Microsoft Agent Framework 的 Agent 抽象，屏蔽具体供应商。</summary>
public interface IChatService
{
    /// <summary>是否已配置可用模型（运行时解析，未配置时前端提示去设置页）。</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default);

    /// <summary>流式生成回复，逐段返回文本增量；可覆盖系统提示词（用于 RAG 注入上下文）。</summary>
    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> history,
        string userMessage,
        CancellationToken cancellationToken,
        string? systemPrompt = null);
}
