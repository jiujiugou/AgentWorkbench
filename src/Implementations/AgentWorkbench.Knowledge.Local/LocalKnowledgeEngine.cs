using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Local.Indexing;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Persistence;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Knowledge.Local;

/// <summary>本地知识引擎：只负责索引的写入与清理，检索执行已移到 Python MCP server。</summary>
/// <remarks>
/// 为什么写入还留在 C#：文档上传、解析、切片、写 FTS/向量都由 <c>Documents</c> 模块完成，
/// Python MCP server 只是以只读方式查询同一个库。把写入也搬走需要 Python 侧接管整个
/// ingest 管道，那是另一次迁移；本次只把「检索」这一层移出去。
/// </remarks>
public sealed class LocalKnowledgeEngine : IKnowledgeEngine
{
    private readonly IDocumentRepository _repository;
    private readonly ChunkIndexer _indexer;
    private readonly IEmbeddingProvider _embedding;
    private readonly KnowledgeOptions _options;

    public LocalKnowledgeEngine(
        IDocumentRepository repository,
        ChunkIndexer indexer,
        IEmbeddingProvider embedding,
        IOptions<KnowledgeOptions> options)
    {
        _repository = repository;
        _indexer = indexer;
        _embedding = embedding;
        _options = options.Value;
    }

    public string Provider => "local";

    /// <summary>检索不在本进程执行；问答统一走 MCP 的 <c>mcp__rag__search_knowledge</c> 工具。</summary>
    public KnowledgeCapabilities Capabilities =>
        new(Retrieve: false, Ask: false, Upload: false, Rerank: _options.Rerank.Enabled);

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
}
