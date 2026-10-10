using System.Diagnostics;
using System.Text.RegularExpressions;
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
        var terms = SplitTerms(query);
        if (terms.Count == 0)
            return [];

        using var connection = _factory.Create();

        // 1) FTS5/BM25：trigram 分词需 >=3 字符，故只用长词构建 MATCH
        var ftsTerms = terms.Where(t => t.Length >= 3).ToList();
        if (ftsTerms.Count > 0)
        {
            var match = string.Join(" OR ", ftsTerms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
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
                _logger.LogDebug("FTS search (MATCH): {HitCount} hits in {ElapsedMs}ms", hits.Count, sw.ElapsedMilliseconds);
                return hits;
            }
        }

        // 2) LIKE 兜底：中文短词（1–2 字）无法被 trigram 命中
        var parameters = new DynamicParameters();
        parameters.Add("limit", limit);
        var clauses = new List<string>(terms.Count);
        for (var i = 0; i < terms.Count; i++)
        {
            var name = $"p{i}";
            clauses.Add($"content LIKE @{name}");
            parameters.Add(name, $"%{terms[i]}%");
        }

        var likeRows = await connection.QueryAsync<FtsRow>(
            $"""
             SELECT chunk_id AS ChunkId, document_id AS DocumentId, 0 AS Score
             FROM chunks_fts
             WHERE {string.Join(" OR ", clauses)}
             LIMIT @limit
             """,
            parameters);
        var likeHits = ToPairs(likeRows);
        _logger.LogDebug("FTS search (LIKE): {HitCount} hits in {ElapsedMs}ms", likeHits.Count, sw.ElapsedMilliseconds);
        return likeHits;
    }

    private static List<string> SplitTerms(string query)
    {
        return Regex.Split(query, @"[\s\p{P}\p{S}]+")
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct()
            .Take(12)
            .ToList();
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
