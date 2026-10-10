using AgentWorkbench.Documents.Parsing;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Persistence;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Documents;

/// <summary>后台单读者解析：出队 → 抽取页文本 → 入库，状态 pending→parsing→parsed|failed。</summary>
public sealed class DocumentParseWorker : BackgroundService
{
    private readonly IDocumentParseQueue _queue;
    private readonly IDocumentRepository _repository;
    private readonly IPdfTextExtractor _extractor;
    private readonly ITextCleaner _cleaner;
    private readonly IChunker _chunker;
    private readonly IKnowledgeEngine _knowledge;
    private readonly StorageOptions _options;
    private readonly ILogger<DocumentParseWorker> _logger;

    public DocumentParseWorker(
        IDocumentParseQueue queue,
        IDocumentRepository repository,
        IPdfTextExtractor extractor,
        ITextCleaner cleaner,
        IChunker chunker,
        IKnowledgeEngine knowledge,
        IOptions<StorageOptions> options,
        ILogger<DocumentParseWorker> logger)
    {
        _queue = queue;
        _repository = repository;
        _extractor = extractor;
        _cleaner = cleaner;
        _chunker = chunker;
        _knowledge = knowledge;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DocumentParseWorker started.");

        await foreach (var documentId in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(documentId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析文档失败 {DocumentId}", documentId);
            }
        }

        _logger.LogInformation("DocumentParseWorker stopped.");
    }

    private async Task ProcessAsync(string documentId, CancellationToken cancellationToken)
    {
        var document = await _repository.GetAsync(documentId);
        if (document is null)
            return;

        await _repository.UpdateStatusAsync(documentId, DocumentStatus.Parsing, null, null, null);

        try
        {
            if (!File.Exists(document.StoredPath))
                throw new FileNotFoundException("上传文件不存在", document.StoredPath);

            var extracted = await Task.Run(() => _extractor.Extract(document.StoredPath), cancellationToken);
            if (extracted.Count > _options.MaxPages)
                throw new InvalidOperationException($"页数 {extracted.Count} 超过上限 {_options.MaxPages}");

            if (extracted.All(p => string.IsNullOrWhiteSpace(p.Text)))
                throw new InvalidOperationException("未检测到文本层，可能是扫描件（P0 仅支持 PDF 文本层，不含 OCR）");

            var cleaned = _cleaner.Clean(extracted);
            var drafts = _chunker.Chunk(cleaned);

            var rawByPage = extracted.ToDictionary(p => p.PageNumber, p => p.Text);
            var pageRecords = cleaned
                .Select(p => new DocumentPageRecord
                {
                    DocumentId = documentId,
                    PageNo = p.PageNumber,
                    RawText = rawByPage.TryGetValue(p.PageNumber, out var raw) ? raw : p.Text,
                    CleanedText = p.Text
                })
                .ToList();

            var chunkRecords = drafts
                .Select(d => new ChunkRecord
                {
                    DocumentId = documentId,
                    Seq = d.Seq,
                    PageFrom = d.PageFrom,
                    PageTo = d.PageTo,
                    Heading = d.Heading,
                    Content = d.Content,
                    CharCount = d.Content.Length,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("o")
                })
                .ToList();

            // 重建索引前先清理旧 chunk 的向量/全文行（重解析时 chunk id 会变化）
            await _knowledge.RemoveDocumentAsync(documentId, cancellationToken);

            await _repository.ReplacePagesAsync(documentId, pageRecords);
            await _repository.ReplaceChunksAsync(documentId, chunkRecords);
            await _repository.UpdateStatusAsync(
                documentId, DocumentStatus.Parsed, null, cleaned.Count, DateTimeOffset.UtcNow.ToString("o"));

            // 建索引：FTS5 + 向量（向量不可用时自动降级）
            await _knowledge.IndexDocumentAsync(documentId, cancellationToken);

            _logger.LogInformation(
                "已解析 {DocumentId}: {Pages} 页, {Chunks} 块", documentId, cleaned.Count, chunkRecords.Count);
        }
        catch (Exception ex)
        {
            await _repository.UpdateStatusAsync(documentId, DocumentStatus.Failed, ex.Message, null, null);
            throw;
        }
    }
}
