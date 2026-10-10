using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;

namespace AgentWorkbench.Knowledge.Abstractions;

/// <summary>知识引擎能力位，前端据此决定按钮是否可用。</summary>
public sealed record KnowledgeCapabilities(bool Retrieve, bool Ask, bool Upload, bool Rerank);

/// <summary>知识库健康状态。</summary>
public sealed record KnowledgeStatus(
    string Provider,
    KnowledgeCapabilities Capabilities,
    bool EmbeddingConfigured,
    string EmbeddingProvider,
    string EmbeddingModel,
    int DocumentCount,
    int IndexedChunkCount);

/// <summary>
/// 可替换知识引擎统一接口（防腐层）：
/// 本地自研（Local）与 AnythingLLM / RAGFlow / OpenContracts 走同一契约。
/// </summary>
public interface IKnowledgeEngine
{
    string Provider { get; }

    KnowledgeCapabilities Capabilities { get; }

    Task<KnowledgeStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task IndexDocumentAsync(string documentId, CancellationToken cancellationToken);

    Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAnswerAsync(
        string query,
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken);
}
