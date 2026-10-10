using System.Diagnostics;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Local.Retrieval;
using AgentWorkbench.Persistence;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Knowledge.Local.Indexing;

/// <summary>
/// 建索引：把 P0 的 chunks 写入 FTS5（trigram）并向量化写入 sqlite-vec。
/// 向量不可用时自动跳过，仅保留全文索引。
/// </summary>
public sealed class ChunkIndexer
{
    private readonly IDocumentRepository _repository;
    private readonly IDbConnectionFactory _factory;
    private readonly IEmbeddingProvider _embedding;
    private readonly VectorIndex _vectorIndex;
    private readonly ILogger<ChunkIndexer> _logger;

    public ChunkIndexer(
        IDocumentRepository repository,
        IDbConnectionFactory factory,
        IEmbeddingProvider embedding,
        VectorIndex vectorIndex,
        ILogger<ChunkIndexer> logger)
    {
        _repository = repository;
        _factory = factory;
        _embedding = embedding;
        _vectorIndex = vectorIndex;
        _logger = logger;
    }

    public bool VectorEnabled => _embedding.IsConfigured && _vectorIndex.Enabled;

    /// <summary>对某个文档重建索引（先调用 <see cref="RemoveDocumentAsync"/> 清理旧向量）。</summary>
    public async Task IndexDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("IndexDocumentAsync started: DocumentId={DocumentId}", documentId);

        var chunks = await _repository.GetChunksAsync(documentId);
        _logger.LogDebug("IndexDocumentAsync: Got {ChunkCount} chunks", chunks.Count);

        await ReplaceFtsAsync(documentId, chunks, cancellationToken);

        if (chunks.Count == 0 || !_embedding.IsConfigured)
        {
            _logger.LogInformation("IndexDocumentAsync completed (FTS only): DocumentId={DocumentId} Chunks={ChunkCount} ElapsedMs={ElapsedMs}", documentId, chunks.Count, sw.ElapsedMilliseconds);
            return;
        }

        try
        {
            var vectors = await _embedding.EmbedAsync(chunks.Select(c => c.Content).ToList(), cancellationToken);
            if (vectors.Length != chunks.Count)
                throw new InvalidOperationException($"Embedding 数量不匹配：{vectors.Length} != {chunks.Count}");

            var records = new List<ChunkVectorRecord>(chunks.Count);
            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                records.Add(new ChunkVectorRecord
                {
                    ChunkId = chunk.Id,
                    DocumentId = chunk.DocumentId,
                    Seq = chunk.Seq,
                    Heading = chunk.Heading,
                    PageFrom = chunk.PageFrom,
                    PageTo = chunk.PageTo,
                    Embedding = vectors[i]
                });
            }

            await _vectorIndex.UpsertAsync(records, cancellationToken);
            _logger.LogInformation("已索引 {DocumentId}: {Chunks} 块（全文 + 向量）ElapsedMs={ElapsedMs}", documentId, chunks.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "文档 {DocumentId} 向量化失败，仅保留全文索引。ElapsedMs={ElapsedMs}", documentId, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>删除文档的 FTS 行与向量（在删除/重解析 chunks 之前调用）。</summary>
    public async Task RemoveDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("RemoveDocumentAsync started: DocumentId={DocumentId}", documentId);

        var chunks = await _repository.GetChunksAsync(documentId);

        using (var connection = _factory.Create())
        {
            await connection.ExecuteAsync(
                "DELETE FROM chunks_fts WHERE document_id = @documentId", new { documentId });
        }

        if (chunks.Count == 0 || !_embedding.IsConfigured)
        {
            _logger.LogDebug("RemoveDocumentAsync completed (FTS only): DocumentId={DocumentId} ElapsedMs={ElapsedMs}", documentId, sw.ElapsedMilliseconds);
            return;
        }

        try
        {
            await _vectorIndex.DeleteAsync(chunks.Select(c => c.Id), cancellationToken);
            _logger.LogInformation("RemoveDocumentAsync completed: DocumentId={DocumentId} Chunks={ChunkCount} ElapsedMs={ElapsedMs}", documentId, chunks.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除文档 {DocumentId} 向量失败。ElapsedMs={ElapsedMs}", documentId, sw.ElapsedMilliseconds);
        }
    }

    public async Task<int> CountIndexedChunksAsync()
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM chunks_fts");
        _logger.LogDebug("CountIndexedChunksAsync: {Count} in {ElapsedMs}ms", count, sw.ElapsedMilliseconds);
        return count;
    }

    private async Task ReplaceFtsAsync(
        string documentId, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "DELETE FROM chunks_fts WHERE document_id = @documentId", new { documentId }, transaction);

        if (chunks.Count > 0)
        {
            await connection.ExecuteAsync(
                "INSERT INTO chunks_fts (content, document_id, chunk_id) VALUES (@Content, @DocumentId, @ChunkId)",
                chunks.Select(c => new { Content = c.Content, DocumentId = c.DocumentId, ChunkId = c.Id }),
                transaction);
        }

        transaction.Commit();
        _logger.LogDebug("ReplaceFtsAsync: DocumentId={DocumentId} Chunks={ChunkCount} ElapsedMs={ElapsedMs}", documentId, chunks.Count, sw.ElapsedMilliseconds);
    }
}
