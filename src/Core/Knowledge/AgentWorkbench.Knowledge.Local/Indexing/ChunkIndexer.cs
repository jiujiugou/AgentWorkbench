using System.Diagnostics;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Local.Retrieval;
using AgentWorkbench.Persistence;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Knowledge.Local.Indexing;

/// <summary>
/// 建索引：把 P0 的 chunks 写入 FTS5（unicode61 + bigram）并向量化写入 sqlite-vec。
/// </summary>
/// <remarks>
/// FTS 的 content 列存的<b>不是原文</b>，是 <see cref="BigramTokenizer"/> 切好后空格连接的 token 串。
/// 取正文永远回 <c>chunks</c> 表（<c>RetrievalHit.Content</c>），不要读 <c>chunks_fts.content</c>。
/// </remarks>
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

    /// <summary>
    /// 对某个文档重建索引（调用前先用 <see cref="RemoveDocumentAsync"/> 清理旧向量）。
    /// </summary>
    /// <remarks>
    /// 除取块本身失败外不再抛：任何向量化失败都以 <see cref="IndexResult.Failure"/> 返回，
    /// 由调用方决定降级、重试还是让文档转 failed——装饰器无权替业务做这个决定。
    /// Warning 记在这里是因为全链路只有这一层同时持有 documentId 和失败归因。
    /// </remarks>
    public async Task<IndexResult> IndexDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("IndexDocumentAsync started: DocumentId={DocumentId}", documentId);

        var chunks = await _repository.GetChunksAsync(documentId);
        _logger.LogDebug("IndexDocumentAsync: Got {ChunkCount} chunks", chunks.Count);

        await ReplaceFtsAsync(documentId, chunks, cancellationToken);

        if (chunks.Count == 0)
        {
            _logger.LogInformation("IndexDocumentAsync：文档 {DocumentId} 无可索引内容，仅清理旧索引。", documentId);
            return new IndexResult(0, 0, 0, null);
        }

        // 未启用向量（Provider = none）是合法配置，不是失败：不尝试、不报错，但覆盖度要如实反映。
        if (!_embedding.IsConfigured)
        {
            _logger.LogInformation(
                "IndexDocumentAsync completed (FTS only，向量未配置): DocumentId={DocumentId} Chunks={ChunkCount} ElapsedMs={ElapsedMs}",
                documentId, chunks.Count, sw.ElapsedMilliseconds);
            return new IndexResult(chunks.Count, chunks.Count, 0, null);
        }

        try
        {
            var vectors = await _embedding.EmbedAsync(chunks.Select(c => c.Content).ToList(), cancellationToken);

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
            _logger.LogInformation(
                "已索引 {DocumentId}: {Chunks} 块（全文 + 向量）ElapsedMs={ElapsedMs}",
                documentId, chunks.Count, sw.ElapsedMilliseconds);
            return new IndexResult(chunks.Count, chunks.Count, chunks.Count, null);
        }
        catch (EmbeddingUnavailableException ex)
        {
            _logger.LogWarning(
                "文档 {DocumentId} 向量化失败（{Code}/{Kind}），仅保留全文索引：{Message} ElapsedMs={ElapsedMs}",
                documentId, ex.Failure.Code, ex.Failure.Kind, ex.Failure.Message, sw.ElapsedMilliseconds);
            return new IndexResult(chunks.Count, chunks.Count, 0, ex.Failure);
        }
        catch (Exception ex)
        {
            var failure = EmbeddingFailure.Classify(ex);
            _logger.LogWarning(ex,
                "文档 {DocumentId} 向量写入失败（{Code}），仅保留全文索引。ElapsedMs={ElapsedMs}",
                documentId, failure.Code, sw.ElapsedMilliseconds);
            return new IndexResult(chunks.Count, chunks.Count, 0, failure);
        }
    }

    /// <summary>
    /// 删除文档的索引（FTS 行与向量），并核对该文档在索引里是否真的没有残留。
    /// </summary>
    /// <remarks>
    /// <para>顺序是「全文 → 向量 → 核对」。全文删除是本地单语句，失败会直接抛；
    /// 向量删除走外部索引，最可能失败，因此必须把结果带回去而不是只打日志。</para>
    /// <para>核对是这条链路的关键：两张表都没有外键，删除静默失效时唯一能发现的办法是再数一遍。</para>
    /// </remarks>
    public async Task<RemoveResult> RemoveDocumentAsync(string documentId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("RemoveDocumentAsync started: DocumentId={DocumentId}", documentId);

        var chunks = await _repository.GetChunksAsync(documentId);

        using (var connection = _factory.Create())
        {
            await connection.ExecuteAsync(
                "DELETE FROM chunks_fts WHERE document_id = @documentId", new { documentId });
        }

        EmbeddingFailure? failure = null;
        if (chunks.Count > 0 && _embedding.IsConfigured)
        {
            try
            {
                await _vectorIndex.DeleteAsync(chunks.Select(c => c.Id), cancellationToken);
            }
            catch (Exception ex)
            {
                var classified = EmbeddingFailure.Classify(ex);
                failure = classified with
                {
                    Code = EmbeddingFailureCode.VectorDeleteFailed,
                    Kind = FailureKind.Permanent
                };
                _logger.LogWarning(ex, "删除文档 {DocumentId} 的向量失败：{Message}", documentId, ex.Message);
            }
        }

        var (ftsRemaining, vectorsRemaining) = await CountRemainingAsync(documentId);

        // FTS 残留永远不会被原谅：全文路无条件可读，残留必然被检索命中。
        if (ftsRemaining > 0)
        {
            failure ??= new EmbeddingFailure(
                EmbeddingFailureCode.VectorDeleteFailed,
                FailureKind.Permanent,
                $"全文索引残留 {ftsRemaining} 行");
        }

        // 向量残留只在向量索引仍可用时才有害；不可用时检索路径同样读不到。
        if (vectorsRemaining > 0 && _vectorIndex.Enabled)
        {
            failure ??= new EmbeddingFailure(
                EmbeddingFailureCode.VectorDeleteFailed,
                FailureKind.Permanent,
                $"向量索引残留 {vectorsRemaining} 条");
        }
        else if (vectorsRemaining > 0)
        {
            _logger.LogWarning(
                "文档 {DocumentId} 向量残留 {Count} 条，但向量索引不可用，检索读不到，按无害处理。",
                documentId, vectorsRemaining);
        }

        _logger.LogInformation(
            "RemoveDocumentAsync completed: DocumentId={DocumentId} Chunks={ChunkCount} FtsRemaining={Fts} VectorsRemaining={Vectors} Cleaned={Cleaned} ElapsedMs={ElapsedMs}",
            documentId, chunks.Count, ftsRemaining, vectorsRemaining,
            ftsRemaining == 0 && (vectorsRemaining == 0 || !_vectorIndex.Enabled), sw.ElapsedMilliseconds);

        return new RemoveResult(ftsRemaining, vectorsRemaining, failure);
    }

    /// <summary>再数一遍索引里还剩什么。判"结果"而不是判"调用是否抛异常"。</summary>
    private async Task<(int Fts, int Vectors)> CountRemainingAsync(string documentId)
    {
        using var connection = _factory.Create();
        var fts = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM chunks_fts WHERE document_id = @documentId", new { documentId });

        int vectors;
        try
        {
            vectors = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM chunks_vec WHERE DocumentId = @documentId", new { documentId });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
            when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            vectors = 0; // 向量集合从未建立，等价于无残留
        }

        return (fts, vectors);
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
            // content 存 bigram 化后的 token 串，不是原文；查询侧必须调同一个 BigramTokenizer
            await connection.ExecuteAsync(
                "INSERT INTO chunks_fts (content, document_id, chunk_id) VALUES (@Content, @DocumentId, @ChunkId)",
                chunks.Select(c => new
                {
                    Content = BigramTokenizer.ToIndexText(c.Content),
                    DocumentId = c.DocumentId,
                    ChunkId = c.Id
                }),
                transaction);
        }

        transaction.Commit();
        _logger.LogDebug("ReplaceFtsAsync: DocumentId={DocumentId} Chunks={ChunkCount} ElapsedMs={ElapsedMs}", documentId, chunks.Count, sw.ElapsedMilliseconds);
    }
}
