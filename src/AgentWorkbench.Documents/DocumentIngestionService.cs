using System.Security.Cryptography;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Persistence;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Documents;

/// <summary>接收上传：落盘 + SHA256 去重 + 入库(pending) + 入队解析。</summary>
public sealed class DocumentIngestionService
{
    private readonly IDocumentRepository _repository;
    private readonly IDocumentParseQueue _queue;
    private readonly StoragePaths _paths;
    private readonly StorageOptions _options;
    private readonly ILogger<DocumentIngestionService> _logger;

    public DocumentIngestionService(
        IDocumentRepository repository,
        IDocumentParseQueue queue,
        StoragePaths paths,
        IOptions<StorageOptions> options,
        ILogger<DocumentIngestionService> logger)
    {
        _repository = repository;
        _queue = queue;
        _paths = paths;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DocumentRecord> IngestAsync(
        Stream content, string fileName, long size, CancellationToken cancellationToken)
    {
        if (size > _options.MaxFileBytes)
            throw new InvalidOperationException($"文件超过上限 {_options.MaxFileBytes / 1024 / 1024} MB");

        var tempPath = Path.Combine(_paths.UploadsDir, $"upload-{Guid.NewGuid():N}.tmp");
        await using (var file = File.Create(tempPath))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        string sha256;
        await using (var read = File.OpenRead(tempPath))
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(read, cancellationToken)).ToLowerInvariant();
        }

        var storedPath = Path.Combine(_paths.UploadsDir, sha256 + ".pdf");
        if (File.Exists(storedPath))
            File.Delete(tempPath);
        else
            File.Move(tempPath, storedPath);

        var document = new DocumentRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = fileName,
            StoredPath = storedPath,
            Sha256 = sha256,
            SizeBytes = new FileInfo(storedPath).Length,
            Status = DocumentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        };

        await _repository.InsertAsync(document);
        await _queue.EnqueueAsync(document.Id, cancellationToken);

        _logger.LogInformation("已接收文档 {FileName} ({Sha256})", fileName, sha256);
        return document;
    }
}
