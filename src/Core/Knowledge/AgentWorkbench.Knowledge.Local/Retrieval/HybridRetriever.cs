using System.Diagnostics;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local.Indexing;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Persistence;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Knowledge.Local.Retrieval;

/// <summary>混合检索：向量（sqlite-vec）∪ 全文（FTS5/BM25）→ RRF 融合。</summary>
public sealed class HybridRetriever
{
    private const int RrfK = 60;

    /// <summary>
    /// 一次查询最多用多少个 bigram 构建 MATCH。bigram 下一个字产生一个 token，
    /// 所以 24 覆盖约 25 字——超过这个长度的问句，多出来的部分对召回已无增量。
    /// </summary>
    private const int MaxQueryTokens = 24;

    private readonly IDocumentRepository _repository;
    private readonly IDbConnectionFactory _factory;
    private readonly IEmbeddingProvider _embedding;
    private readonly VectorIndex _vectorIndex;
    private readonly IReranker _reranker;
    private readonly KnowledgeOptions _options;
    private readonly ILogger<HybridRetriever> _logger;

    public HybridRetriever(
        IDocumentRepository repository,
        IDbConnectionFactory factory,
        IEmbeddingProvider embedding,
        VectorIndex vectorIndex,
        IReranker reranker,
        IOptions<KnowledgeOptions> options,
        ILogger<HybridRetriever> logger)
    {
        _repository = repository;
        _factory = factory;
        _embedding = embedding;
        _vectorIndex = vectorIndex;
        _reranker = reranker;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(
        string query, int topK, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var sw = Stopwatch.StartNew();
        _logger.LogDebug("Retrieve started: Query='{Query}' TopK={TopK}", query, topK);

        var candidateK = Math.Max(_options.CandidateK, topK);

        var vectorHits = await VectorSearchAsync(query, candidateK, cancellationToken);
        var textHits = await FtsSearchAsync(query, candidateK, cancellationToken);

        _logger.LogDebug("Vector hits: {Count}, Text hits: {Count}", vectorHits.Count, textHits.Count);

        var fused = new Dictionary<long, Fusion>();

        for (var rank = 0; rank < vectorHits.Count; rank++)
            Accumulate(fused, vectorHits[rank].Key, rrf: 1.0 / (RrfK + rank + 1), vectorScore: vectorHits[rank].Value, textScore: null);

        for (var rank = 0; rank < textHits.Count; rank++)
            Accumulate(fused, textHits[rank].Key, rrf: 1.0 / (RrfK + rank + 1), vectorScore: null, textScore: textHits[rank].Value);

        if (fused.Count == 0)
        {
            _logger.LogInformation("Retrieve: no candidates found for query '{Query}' in {ElapsedMs}ms", query, sw.ElapsedMilliseconds);
            return [];
        }

        // 先取较宽候选供（可选）重排，再截断到 topK
        var candidateCount = _reranker.Enabled ? Math.Max(topK * 3, topK) : topK;
        var ordered = fused
            .OrderByDescending(kv => kv.Value.Score)
            .Take(candidateCount)
            .ToList();

        var ids = ordered.Select(kv => kv.Key).ToList();
        var chunks = (await _repository.GetChunksByIdsAsync(ids)).ToDictionary(c => c.Id);
        var documentNames = new Dictionary<string, string>();

        var hits = new List<RetrievalHit>(ordered.Count);
        foreach (var (chunkId, fusion) in ordered)
        {
            if (!chunks.TryGetValue(chunkId, out var chunk))
                continue;

            if (!documentNames.TryGetValue(chunk.DocumentId, out var fileName))
            {
                var document = await _repository.GetAsync(chunk.DocumentId);
                fileName = document?.FileName ?? chunk.DocumentId;
                documentNames[chunk.DocumentId] = fileName;
            }

            hits.Add(new RetrievalHit
            {
                ChunkId = chunk.Id,
                DocumentId = chunk.DocumentId,
                FileName = fileName,
                Seq = chunk.Seq,
                Heading = chunk.Heading,
                PageFrom = chunk.PageFrom,
                PageTo = chunk.PageTo,
                Content = chunk.Content,
                VectorScore = fusion.VectorScore,
                TextScore = fusion.TextScore,
                FusedScore = fusion.Score
            });
        }

        var reranked = await _reranker.RerankAsync(query, hits, topK, cancellationToken);
        _logger.LogInformation("Retrieve completed: Query='{Query}' Hits={HitCount} Reranked={Reranked} ElapsedMs={ElapsedMs}", query, hits.Count, _reranker.Enabled, sw.ElapsedMilliseconds);
        return reranked;
    }

    private async Task<List<KeyValuePair<long, double>>> VectorSearchAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        if (!_embedding.IsConfigured || !_vectorIndex.Enabled)
        {
            _logger.LogDebug("Vector search skipped: EmbeddingConfigured={Configured} VectorIndexEnabled={Enabled}", _embedding.IsConfigured, _vectorIndex.Enabled);
            return [];
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var vectors = await _embedding.EmbedAsync([query], cancellationToken);
            var results = await _vectorIndex.SearchAsync(vectors[0], limit, cancellationToken);
            var hits = results
                .Where(r => (r.Score ?? double.MaxValue) <= _options.MaxVectorDistance)
                .Select(r => new KeyValuePair<long, double>(r.Record.ChunkId, r.Score ?? 0))
                .ToList();
            _logger.LogDebug("Vector search: {HitCount} hits in {ElapsedMs}ms", hits.Count, sw.ElapsedMilliseconds);
            return hits;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "向量检索失败，退化为纯全文检索。");
            return [];
        }
    }

    private async Task<List<KeyValuePair<long, double>>> FtsSearchAsync(
        string query, int limit, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        // 索引与查询必须调同一个分词器。两边不一致时结果是 0 命中，且不会报任何错。
        var terms = BigramTokenizer.Tokens(query)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxQueryTokens)
            .ToList();

        using var connection = _factory.Create();

        // 1) FTS5/BM25：unicode61 下边界由写入方（bigram）决定，不再有"长度 >= 3"的限制
        if (terms.Count > 0)
        {
            var match = string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
            var rows = await connection.QueryAsync<FtsRow>(
                """
                SELECT chunk_id AS ChunkId, document_id AS DocumentId, -bm25(chunks_fts) AS Score
                FROM chunks_fts
                WHERE chunks_fts MATCH @match
                ORDER BY bm25(chunks_fts)
                LIMIT @limit
                """,
                new { match, limit });
            var hits = ToPairs(rows);
            if (hits.Count > 0)
            {
                _logger.LogDebug("FTS search (MATCH): tokens={TokenCount} hits={HitCount} in {ElapsedMs}ms",
                    terms.Count, hits.Count, sw.ElapsedMilliseconds);
                return hits;
            }
        }

        // 2) LIKE 兜底：只服务切不出 bigram 的单字查询（如「泵」）。
        //    单字不入索引是有意的——「的/是/在」会命中几乎所有块，只会稀释 bm25。
        //    ORDER BY chunk_id 不是为了相关性（这里没有相关性，Score 恒 0），而是让同一查询
        //    两次运行结果一致，否则命中顺序等于表扫描顺序，不可复算。
        var singleChars = BigramTokenizer.SingleHanChars(query);
        if (singleChars.Count == 0)
            return [];

        var parameters = new DynamicParameters();
        parameters.Add("limit", limit);
        var clauses = new List<string>(singleChars.Count);
        for (var i = 0; i < singleChars.Count; i++)
        {
            var name = $"p{i}";
            clauses.Add($"content LIKE @{name}");
            parameters.Add(name, $"%{singleChars[i]}%");
        }

        var likeRows = await connection.QueryAsync<FtsRow>(
            $"""
             SELECT chunk_id AS ChunkId, document_id AS DocumentId, 0 AS Score
             FROM chunks_fts
             WHERE {string.Join(" OR ", clauses)}
             ORDER BY chunk_id
             LIMIT @limit
             """,
            parameters);
        var likeHits = ToPairs(likeRows);
        _logger.LogDebug("FTS search (LIKE fallback): chars={CharCount} hits={HitCount} in {ElapsedMs}ms",
            singleChars.Count, likeHits.Count, sw.ElapsedMilliseconds);
        return likeHits;
    }

    private static List<KeyValuePair<long, double>> ToPairs(IEnumerable<FtsRow> rows)
        => rows.Select(r => new KeyValuePair<long, double>(r.ChunkId, r.Score)).ToList();

    private static void Accumulate(
        Dictionary<long, Fusion> fused, long chunkId, double rrf, double? vectorScore, double? textScore)
    {
        fused.TryGetValue(chunkId, out var current);
        fused[chunkId] = new Fusion(
            current.Score + rrf,
            vectorScore ?? current.VectorScore,
            textScore ?? current.TextScore);
    }

    private readonly record struct Fusion(double Score, double? VectorScore, double? TextScore);

    private sealed class FtsRow
    {
        public long ChunkId { get; set; }
        public string DocumentId { get; set; } = string.Empty;
        public double Score { get; set; }
    }
}
