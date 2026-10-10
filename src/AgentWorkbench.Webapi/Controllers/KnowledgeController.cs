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
            result.Add(new
            {
                id = document.Id,
                fileName = document.FileName,
                status = document.Status,
                pageCount = document.PageCount,
                chunkCount,
                indexed = document.Status == DocumentStatus.Parsed && chunkCount > 0
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

        await _engine.RemoveDocumentAsync(id, cancellationToken);
        await _engine.IndexDocumentAsync(id, cancellationToken);
        _logger.LogInformation("Reindex completed: {DocumentId} in {ElapsedMs}ms", id, sw.ElapsedMilliseconds);
        return Accepted(new { id, status = await _engine.GetStatusAsync(cancellationToken) });
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
