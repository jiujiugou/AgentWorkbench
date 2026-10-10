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

/// <summary>本地自研知识引擎：FTS5 + sqlite-vec 混合检索 + 带引用问答。</summary>
public sealed class LocalKnowledgeEngine : IKnowledgeEngine
{
    private readonly IDocumentRepository _repository;
    private readonly ChunkIndexer _indexer;
    private readonly HybridRetriever _retriever;
    private readonly IRagService _rag;
    private readonly IEmbeddingProvider _embedding;
    private readonly KnowledgeOptions _options;

    public LocalKnowledgeEngine(
        IDocumentRepository repository,
        ChunkIndexer indexer,
        HybridRetriever retriever,
        IRagService rag,
        IEmbeddingProvider embedding,
        IOptions<KnowledgeOptions> options)
    {
        _repository = repository;
        _indexer = indexer;
        _retriever = retriever;
        _rag = rag;
        _embedding = embedding;
        _options = options.Value;
    }

    public string Provider => "local";

    public KnowledgeCapabilities Capabilities =>
        new(Retrieve: true, Ask: true, Upload: false, Rerank: _options.Rerank.Enabled);

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

    public Task IndexDocumentAsync(string documentId, CancellationToken cancellationToken)
        => _indexer.IndexDocumentAsync(documentId, cancellationToken);

    public Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken)
        => _indexer.RemoveDocumentAsync(documentId, cancellationToken);

    public Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(
        string query, int topK, CancellationToken cancellationToken)
        => _retriever.RetrieveAsync(query, topK <= 0 ? _options.TopK : topK, cancellationToken);

    public IAsyncEnumerable<string> StreamAnswerAsync(
        string query, IReadOnlyList<ChatTurn> history, CancellationToken cancellationToken)
        => _rag.StreamAnswerAsync(query, history, cancellationToken);
}
