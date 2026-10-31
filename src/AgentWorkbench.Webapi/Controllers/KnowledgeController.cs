using System.Diagnostics;
using System.Text;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local;
using AgentWorkbench.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Webapi.Controllers;

public sealed record RetrieveRequest(string Query, int? TopK);
public sealed record AskRequest(string Query, List<ChatTurn>? History);

[ApiController]
[Route("api/knowledge")]
public sealed class KnowledgeController : ControllerBase
{
    private readonly IKnowledgeEngine _engine;
    private readonly IRagService _rag;
    private readonly IDocumentRepository _documents;
    private readonly ILogger<KnowledgeController> _logger;

    public KnowledgeController(
        IKnowledgeEngine engine,
        IRagService rag,
        IDocumentRepository documents,
        ILogger<KnowledgeController> logger)
    {
        _engine = engine;
        _rag = rag;
        _documents = documents;
        _logger = logger;
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var status = await _engine.GetStatusAsync(cancellationToken);
        _logger.LogDebug("Health check: {ElapsedMs}ms", sw.ElapsedMilliseconds);
        return Ok(status);
    }

    [HttpGet("documents")]
    public async Task<IActionResult> Documents(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documents = await _documents.ListAsync();
        var result = new List<object>(documents.Count);
        foreach (var document in documents)
        {
            var chunkCount = await _documents.CountChunksAsync(document.Id);
            var vectorCount = await _documents.CountVectorsAsync(document.Id);
            var indexState = IndexStates.Describe(document.Status, chunkCount, vectorCount);
            result.Add(new
            {
                id = document.Id,
                fileName = document.FileName,
                status = document.Status,
                pageCount = document.PageCount,
                chunkCount,
                vectorCount,
                indexState,
                indexed = indexState == IndexStates.Full
            });
        }
        _logger.LogDebug("List knowledge documents: {Count} in {ElapsedMs}ms", result.Count, sw.ElapsedMilliseconds);
        return Ok(result);
    }

    [HttpPost("documents/{id}/index")]
    public async Task<IActionResult> Reindex(string id, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var document = await _documents.GetAsync(id);
        if (document is null)
        {
            _logger.LogWarning("Reindex document not found: {DocumentId}", id);
            return NotFound();
        }

        // 重建前先清旧向量/全文行：chunk id 会随重新分块变化，不清理会留下指向已删 chunk 的孤儿向量。
        var removal = await _engine.RemoveDocumentAsync(id, cancellationToken);
        if (!removal.Cleaned)
        {
            _logger.LogWarning(
                "Reindex aborted: {DocumentId} 旧索引未清干净 Fts={Fts} Vectors={Vectors} Code={Code}",
                id, removal.FtsRemaining, removal.VectorsRemaining, removal.Failure?.Code);
            return Conflict(new
            {
                message = "旧索引未能完全清理，已中止重建以免留下无法检索的孤儿向量。",
                code = removal.Failure?.Code
            });
        }

        var index = await _engine.IndexDocumentAsync(id, cancellationToken);
        _logger.LogInformation(
            "Reindex completed: {DocumentId} Chunks={Chunks} Fts={Fts} Vectors={Vectors} Code={Code} in {ElapsedMs}ms",
            id, index.ChunkCount, index.FtsCount, index.VectorCount, index.Failure?.Code, sw.ElapsedMilliseconds);
        return Accepted(new
        {
            id,
            chunkCount = index.ChunkCount,
            vectorCount = index.VectorCount,
            indexState = index.Failure is null && index.ChunkCount > 0 && index.VectorCount >= index.ChunkCount
                ? IndexStates.Full
                : index.Failure is null ? IndexStates.TextOnly : IndexStates.Partial,
            failure = index.Failure?.Code,
            failureMessage = index.Failure?.Message,
            status = await _engine.GetStatusAsync(cancellationToken)
        });
    }

    [HttpPost("retrieve")]
    public async Task<IActionResult> Retrieve([FromBody] RetrieveRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
        {
            _logger.LogWarning("Retrieve failed: empty query");
            return BadRequest(new { message = "query 不能为空" });
        }

        _logger.LogInformation("Retrieve: Query='{Query}' TopK={TopK}", request.Query, request.TopK);
        var hits = await _rag.RetrieveAsync(request.Query, request.TopK ?? 0, cancellationToken);
        _logger.LogInformation("Retrieve completed: {HitCount} hits in {ElapsedMs}ms", hits.Count, sw.ElapsedMilliseconds);
        return Ok(hits.Select(ToSource));
    }

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
        {
            _logger.LogWarning("Ask failed: empty query");
            return BadRequest(new { message = "query 不能为空" });
        }

        _logger.LogInformation("Ask: Query='{Query}' HistoryTurns={HistoryCount}", request.Query, request.History?.Count ?? 0);
        var sources = await _rag.RetrieveAsync(request.Query, 0, cancellationToken);
        var builder = new StringBuilder();
        await foreach (var delta in _rag.StreamAnswerAsync(request.Query, request.History ?? [], cancellationToken))
            builder.Append(delta);

        _logger.LogInformation("Ask completed: AnswerLength={Length} Sources={SourceCount} ElapsedMs={ElapsedMs}", builder.Length, sources.Count, sw.ElapsedMilliseconds);
        return Ok(new { answer = builder.ToString(), sources = sources.Select(ToSource) });
    }

    private static object ToSource(RetrievalHit hit) => new
    {
        chunkId = hit.ChunkId,
        documentId = hit.DocumentId,
        fileName = hit.FileName,
        seq = hit.Seq,
        heading = hit.Heading,
        pageFrom = hit.PageFrom,
        pageTo = hit.PageTo,
        vectorScore = hit.VectorScore,
        textScore = hit.TextScore,
        fusedScore = hit.FusedScore,
        content = hit.Content
    };
}
