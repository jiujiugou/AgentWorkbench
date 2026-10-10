namespace AgentWorkbench.Domain;

/// <summary>一轮对话（用于回放历史 / RAG 上下文）。</summary>
public sealed record ChatTurn(string Role, string Content);
