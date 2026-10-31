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

        // 四种拒收必须能分辨：前端要按 code 给不同提示，靠 message 匹配字串会在改文案时断掉。
        // 顺序也是契约的一部分——先判「有没有文件」，再判内容，最后判大小：
        // 空文件同时满足「格式不支持」时，用户该看到的是「文件为空」。
        if (file is null)
        {
            _logger.LogWarning("Upload failed: no file field");
            return BadRequest(new { code = DocumentErrorCodes.NoFile, message = "请求里没有 file 字段" });
        }
        if (file.Length == 0)
        {
            _logger.LogWarning("Upload failed: empty file - {FileName}", file.FileName);
            return BadRequest(new { code = DocumentErrorCodes.FileEmpty, message = "文件为空" });
        }

        string[] supported = [".pdf", ".txt", ".md", ".json"];
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!supported.Contains(ext))
        {
            _logger.LogWarning("Upload failed: unsupported type - {FileName}", file.FileName);
            return BadRequest(new
            {
                code = DocumentErrorCodes.FileTypeUnsupported,
                message = "仅支持 PDF / TXT / MD / JSON（P0 文本层，JSON 约定 content 或 text 字段为正文）"
            });
        }
        if (file.Length > _options.MaxFileBytes)
        {
            _logger.LogWarning("Upload failed: file too large - {FileName} {SizeBytes} bytes", file.FileName, file.Length);
            return BadRequest(new
            {
                code = DocumentErrorCodes.FileTooLarge,
                message = $"文件超过 {_options.MaxFileBytes / 1024 / 1024} MB"
            });
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
            result.Add(ToDto(
                document,
                await _repository.CountChunksAsync(document.Id),
                await _repository.CountVectorsAsync(document.Id)));
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
        return Ok(ToDto(document, await _repository.CountChunksAsync(id), await _repository.CountVectorsAsync(id)));
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

        // 先清索引，确认真的没有残留再删记录。
        // 反过来（先删记录）的风险更高：fts / 向量表都没有外键，删不掉时不会报错，
        // 只会留下"列表里看不见、检索却能命中"的幽灵，且用户没有任何入口再清一次。
        var removal = await _knowledge.RemoveDocumentAsync(id, cancellationToken);
        if (!removal.Cleaned)
        {
            _logger.LogError(
                "删除中止：{DocumentId} 索引未清干净 Fts={Fts} Vectors={Vectors} Code={Code} Message={Message}",
                id, removal.FtsRemaining, removal.VectorsRemaining, removal.Failure?.Code, removal.Failure?.Message);
            return Conflict(new
            {
                message = "索引未能完全清理，已保留记录以免检索命中已删内容，请稍后重试。",
                code = removal.Failure?.Code,
                ftsRemaining = removal.FtsRemaining,
                vectorsRemaining = removal.VectorsRemaining
            });
        }

        await _repository.DeleteAsync(id);

        // 文件删不掉不影响一致性：记录与索引都已清空，残留文件只是磁盘垃圾。
        try
        {
            if (System.IO.File.Exists(document.StoredPath))
                System.IO.File.Delete(document.StoredPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "删除文件失败（记录与索引已清理）：{Path}", document.StoredPath);
        }

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

    private static object ToDto(DocumentRecord document, int? chunkCount = null, int vectorCount = 0) => new
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
        chunkCount,
        vectorCount,
        indexState = IndexStates.Describe(document.Status, chunkCount ?? 0, vectorCount)
    };
}
