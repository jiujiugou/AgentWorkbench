using AgentWorkbench.Documents.Parsing;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
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
    private readonly ITextFileExtractor _textExtractor;
    private readonly ITextCleaner _cleaner;
    private readonly IChunker _chunker;
    private readonly IKnowledgeEngine _knowledge;
    private readonly StorageOptions _options;
    private readonly ILogger<DocumentParseWorker> _logger;

    public DocumentParseWorker(
        IDocumentParseQueue queue,
        IDocumentRepository repository,
        IPdfTextExtractor extractor,
        ITextFileExtractor textExtractor,
        ITextCleaner cleaner,
        IChunker chunker,
        IKnowledgeEngine knowledge,
        IOptions<StorageOptions> options,
        ILogger<DocumentParseWorker> logger)
    {
        _queue = queue;
        _repository = repository;
        _extractor = extractor;
        _textExtractor = textExtractor;
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
            // 抽取分流：纯文本（txt/md/json）直接读，PDF 走文本层抽取；后续清洗/分块/索引共用
            IReadOnlyList<ExtractedPage> extracted;
            if (_textExtractor.Supports(document.FileName))
            {
                extracted = await _textExtractor.ExtractAsync(document.StoredPath, document.FileName, cancellationToken);
            }
            else
            {
                if (!File.Exists(document.StoredPath))
                    throw new FileNotFoundException("上传文件不存在", document.StoredPath);

                extracted = await Task.Run(() => _extractor.Extract(document.StoredPath), cancellationToken);
            }

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
            var removal = await _knowledge.RemoveDocumentAsync(documentId, cancellationToken);
            if (!removal.Cleaned)
            {
                // 旧索引清不干净就重建，会留下指向已删分块的孤儿向量：检索命中后回表取不到正文，
                // 只会白占候选名额。宁可这次重解析失败（状态可见、可重试），也不制造界面发现不了的幽灵。
                throw new InvalidOperationException(
                    $"重建前清理旧索引失败（{removal.Failure?.Code}）：全文残留 {removal.FtsRemaining} / 向量残留 {removal.VectorsRemaining}");
            }

            await _repository.ReplacePagesAsync(documentId, pageRecords);
            await _repository.ReplaceChunksAsync(documentId, chunkRecords);

            // 索引结果决定终态：解析完成 ≠ 索引完成。
            // 永久失败（鉴权 / 模型不存在 / 维度不符）重试无意义，让文档显式 failed，
            // 而不是留下「解析成功、语义检索却残着」的中间态；
            // 暂时失败（网络 / 超时 / 限流）保留 parsed，由列表的「仅全文」暴露，恢复后重建即可补齐。
            var index = await _knowledge.IndexDocumentAsync(documentId, cancellationToken);

            if (index.Failure is { Kind: FailureKind.Permanent })
            {
                await _repository.UpdateStatusAsync(
                    documentId, DocumentStatus.Failed,
                    $"索引失败（{index.Failure.Code}）：{index.Failure.Message}",
                    cleaned.Count, DateTimeOffset.UtcNow.ToString("o"));
                _logger.LogError(
                    "文档 {DocumentId} 索引永久失败：{Code} {Message}（已解析 {Pages} 页 / {Chunks} 块，全文仍可用）",
                    documentId, index.Failure.Code, index.Failure.Message, cleaned.Count, chunkRecords.Count);
                return;
            }

            if (index.Failure is not null)
            {
                _logger.LogWarning(
                    "文档 {DocumentId} 仅完成全文索引：{Code}/{Kind}，服务恢复后可重建补向量。",
                    documentId, index.Failure.Code, index.Failure.Kind);
            }

            await _repository.UpdateStatusAsync(
                documentId, DocumentStatus.Parsed, null, cleaned.Count, DateTimeOffset.UtcNow.ToString("o"));

            _logger.LogInformation(
                "已解析 {DocumentId}: {Pages} 页, {Chunks} 块, 向量 {Vectors} 块",
                documentId, cleaned.Count, chunkRecords.Count, index.VectorCount);
        }
        catch (Exception ex)
        {
            await _repository.UpdateStatusAsync(documentId, DocumentStatus.Failed, ex.Message, null, null);
            throw;
        }
    }
}
