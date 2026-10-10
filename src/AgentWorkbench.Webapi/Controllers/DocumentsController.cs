using System.Diagnostics;
using AgentWorkbench.Documents;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Webapi.Controllers;

[ApiController]
[Route("api/documents")]
public sealed class DocumentsController : ControllerBase
{
    private readonly IDocumentRepository _repository;
    private readonly DocumentIngestionService _ingestion;
    private readonly IDocumentParseQueue _queue;
    private readonly IKnowledgeEngine _knowledge;
    private readonly StorageOptions _options;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        IDocumentRepository repository,
        DocumentIngestionService ingestion,
        IDocumentParseQueue queue,
        IKnowledgeEngine knowledge,
        IOptions<StorageOptions> options,
        ILogger<DocumentsController> logger)
    {
        _repository = repository;
        _ingestion = ingestion;
        _queue = queue;
        _knowledge = knowledge;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost]
    [RequestSizeLimit(64L * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (file is null || file.Length == 0)
        {
            _logger.LogWarning("Upload failed: empty file");
            return BadRequest(new { message = "文件为空" });
        }
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Upload failed: not a PDF - {FileName}", file.FileName);
            return BadRequest(new { message = "仅支持 PDF（P0 文本层）" });
        }
        if (file.Length > _options.MaxFileBytes)
        {
            _logger.LogWarning("Upload failed: file too large - {FileName} {SizeBytes} bytes", file.FileName, file.Length);
            return BadRequest(new { message = $"文件超过 {_options.MaxFileBytes / 1024 / 1024} MB" });
        }

        _logger.LogInformation("Upload started: {FileName} {SizeBytes} bytes", file.FileName, file.Length);
        await using var stream = file.OpenReadStream();
        var document = await _ingestion.IngestAsync(stream, file.FileName, file.Length, cancellationToken);
        _logger.LogInformation("Upload accepted: {DocumentId} in {ElapsedMs}ms", document.Id, sw.ElapsedMilliseconds);
        return Accepted(ToDto(document));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documents = await _repository.ListAsync();
        var result = new List<object>(documents.Count);
        foreach (var document in documents)
            result.Add(ToDto(document, await _repository.CountChunksAsync(document.Id)));
        _logger.LogDebug("List documents: {Count} in {ElapsedMs}ms", result.Count, sw.ElapsedMilliseconds);
        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var document = await _repository.GetAsync(id);
        if (document is null)
        {
            _logger.LogWarning("Get document not found: {DocumentId}", id);
            return NotFound();
        }
        _logger.LogDebug("Get document: {DocumentId} in {ElapsedMs}ms", id, sw.ElapsedMilliseconds);
        return Ok(ToDto(document, await _repository.CountChunksAsync(id)));
    }

    [HttpGet("{id}/pages")]
    public async Task<IActionResult> Pages(string id, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var pages = await _repository.GetPagesAsync(id);
        _logger.LogDebug("Get pages: {DocumentId} Count={Count} in {ElapsedMs}ms", id, pages.Count, sw.ElapsedMilliseconds);
        return Ok(pages.Select(p => new
        {
            pageNo = p.PageNo,
            rawText = p.RawText,
            cleanedText = p.CleanedText
        }));
    }

    [HttpGet("{id}/chunks")]
    public async Task<IActionResult> Chunks(string id, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var chunks = await _repository.GetChunksAsync(id);
        _logger.LogDebug("Get chunks: {DocumentId} Count={Count} in {ElapsedMs}ms", id, chunks.Count, sw.ElapsedMilliseconds);
        return Ok(chunks.Select(c => new
        {
            seq = c.Seq,
            pageFrom = c.PageFrom,
            pageTo = c.PageTo,
            heading = c.Heading,
            content = c.Content,
            charCount = c.CharCount
        }));
    }

    [HttpPost("{id}/reparse")]
    public async Task<IActionResult> Reparse(string id, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var document = await _repository.GetAsync(id);
        if (document is null)
        {
            _logger.LogWarning("Reparse document not found: {DocumentId}", id);
            return NotFound();
        }

        await _repository.UpdateStatusAsync(id, DocumentStatus.Pending, null, null, null);
        await _queue.EnqueueAsync(id, cancellationToken);
        _logger.LogInformation("Reparse enqueued: {DocumentId} in {ElapsedMs}ms", id, sw.ElapsedMilliseconds);
        return Accepted(new { id, status = DocumentStatus.Pending });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var document = await _repository.GetAsync(id);
        if (document is null)
        {
            _logger.LogWarning("Delete document not found: {DocumentId}", id);
            return NotFound();
        }

        await _knowledge.RemoveDocumentAsync(id, cancellationToken);
        await _repository.DeleteAsync(id);
        if (System.IO.File.Exists(document.StoredPath))
            System.IO.File.Delete(document.StoredPath);
        _logger.LogInformation("Delete document: {DocumentId} in {ElapsedMs}ms", id, sw.ElapsedMilliseconds);
        return NoContent();
    }

    [HttpGet("{id}/file")]
    public async Task<IActionResult> Download(string id, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var document = await _repository.GetAsync(id);
        if (document is null || !System.IO.File.Exists(document.StoredPath))
        {
            _logger.LogWarning("Download not found: {DocumentId}", id);
            return NotFound();
        }
        _logger.LogDebug("Download: {DocumentId} in {ElapsedMs}ms", id, sw.ElapsedMilliseconds);
        return PhysicalFile(document.StoredPath, "application/pdf", document.FileName);
    }

    private static object ToDto(DocumentRecord document, int? chunkCount = null) => new
    {
        id = document.Id,
        fileName = document.FileName,
        status = document.Status,
        pageCount = document.PageCount,
        sizeBytes = document.SizeBytes,
        sha256 = document.Sha256,
        error = document.Error,
        createdAt = document.CreatedAt,
        parsedAt = document.ParsedAt,
        chunkCount
    };
}
