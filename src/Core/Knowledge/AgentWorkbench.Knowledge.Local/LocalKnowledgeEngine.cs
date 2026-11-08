using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Local.Indexing;
using AgentWorkbench.Knowledge.Local.Retrieval;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Persistence;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Knowledge.Local;

/// <summary>本地自研知识引擎：FTS5 + sqlite-vec 混合检索（问答由 harness 的 search_knowledge 工具承担）。</summary>
public sealed class LocalKnowledgeEngine : IKnowledgeEngine
{
    private readonly IDocumentRepository _repository;
    private readonly ChunkIndexer _indexer;
    private readonly HybridRetriever _retriever;
    private readonly IEmbeddingProvider _embedding;
    private readonly KnowledgeOptions _options;

    public LocalKnowledgeEngine(
        IDocumentRepository repository,
        ChunkIndexer indexer,
        HybridRetriever retriever,
        IEmbeddingProvider embedding,
        IOptions<KnowledgeOptions> options)
    {
        _repository = repository;
        _indexer = indexer;
        _retriever = retriever;
        _embedding = embedding;
        _options = options.Value;
    }

    public string Provider => "local";

    /// <summary>
    /// <c>Ask</c> 恒为 false：知识引擎本身不再提供问答端点（<c>POST /api/knowledge/ask</c> 已删除），
    /// 问答统一走 harness 的 <c>search_knowledge</c> 工具。这个位保留只为不改变 API 形状。
    /// </summary>
    public KnowledgeCapabilities Capabilities =>
        new(Retrieve: true, Ask: false, Upload: false, Rerank: _options.Rerank.Enabled);

    public async Task<KnowledgeStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var documents = await _repository.ListAsync();
        var indexed = await _indexer.CountIndexedChunksAsync();
        return new KnowledgeStatus(
            Provider,
            Capabilities,
            EmbeddingConfigured: _embedding.IsConfigured && _indexer.VectorEnabled,
            EmbeddingProvider: _embedding.Name,
            EmbeddingModel: _embedding.Model,
            DocumentCount: documents.Count,
            IndexedChunkCount: indexed);
    }

    public Task<IndexResult> IndexDocumentAsync(string documentId, CancellationToken cancellationToken)
        => _indexer.IndexDocumentAsync(documentId, cancellationToken);

    public Task<RemoveResult> RemoveDocumentAsync(string documentId, CancellationToken cancellationToken)
        => _indexer.RemoveDocumentAsync(documentId, cancellationToken);

    public Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(
        string query, int topK, CancellationToken cancellationToken)
        => _retriever.RetrieveAsync(query, topK <= 0 ? _options.TopK : topK, cancellationToken);
}
