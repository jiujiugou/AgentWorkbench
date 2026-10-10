using Dapper;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AgentWorkbench.Persistence;

public interface IDocumentRepository
{
    Task InsertAsync(DocumentRecord document);
    Task UpdateStatusAsync(string id, string status, string? error, int? pageCount, string? parsedAt);
    Task<DocumentRecord?> GetAsync(string id);
    Task<IReadOnlyList<DocumentRecord>> ListAsync();
    Task<bool> DeleteAsync(string id);
    Task ReplacePagesAsync(string documentId, IReadOnlyList<DocumentPageRecord> pages);
    Task<IReadOnlyList<DocumentPageRecord>> GetPagesAsync(string documentId);
    Task ReplaceChunksAsync(string documentId, IReadOnlyList<ChunkRecord> chunks);
    Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(string documentId);
    Task<IReadOnlyList<ChunkRecord>> GetChunksByIdsAsync(IReadOnlyList<long> ids);
    Task<int> CountChunksAsync(string documentId);
}

public sealed class DocumentRepository : IDocumentRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DocumentRepository> _logger;

    public DocumentRepository(IDbConnectionFactory factory, ILogger<DocumentRepository> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InsertAsync(DocumentRecord document)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = document.Id });
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            INSERT INTO documents (id, file_name, stored_path, sha256, size_bytes, status, page_count, error, created_at, parsed_at)
            VALUES (@Id, @FileName, @StoredPath, @Sha256, @SizeBytes, @Status, @PageCount, @Error, @CreatedAt, @ParsedAt)
            """, document);
        _logger.LogInformation("Inserted document {FileName} ({SizeBytes} bytes) in {ElapsedMs}ms", document.FileName, document.SizeBytes, sw.ElapsedMilliseconds);
    }

    public async Task UpdateStatusAsync(string id, string status, string? error, int? pageCount, string? parsedAt)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = id });
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        await connection.ExecuteAsync(
            """
            UPDATE documents
            SET status = @status,
                error = @error,
                page_count = COALESCE(@pageCount, page_count),
                parsed_at = COALESCE(@parsedAt, parsed_at)
            WHERE id = @id
            """, new { id, status, error, pageCount, parsedAt });
        _logger.LogInformation("Updated document status to {Status} (pages={PageCount}) in {ElapsedMs}ms", status, pageCount, sw.ElapsedMilliseconds);
    }

    public async Task<DocumentRecord?> GetAsync(string id)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = id });
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var result = await connection.QuerySingleOrDefaultAsync<DocumentRecord>(
            "SELECT * FROM documents WHERE id = @id", new { id });
        _logger.LogDebug("Get document: {Found} in {ElapsedMs}ms", result is not null, sw.ElapsedMilliseconds);
        return result;
    }

    public async Task<IReadOnlyList<DocumentRecord>> ListAsync()
    {
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<DocumentRecord>(
            "SELECT * FROM documents ORDER BY created_at DESC");
        var list = rows.AsList();
        _logger.LogDebug("Listed {Count} documents in {ElapsedMs}ms", list.Count, sw.ElapsedMilliseconds);
        return list;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = id });
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var affected = await connection.ExecuteAsync("DELETE FROM documents WHERE id = @id", new { id });
        var deleted = affected > 0;
        _logger.LogInformation("Delete document: {Deleted} in {ElapsedMs}ms", deleted, sw.ElapsedMilliseconds);
        return deleted;
    }

    public async Task ReplacePagesAsync(string documentId, IReadOnlyList<DocumentPageRecord> pages)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = documentId });
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM document_pages WHERE document_id = @documentId", new { documentId }, transaction);
        await connection.ExecuteAsync(
            """
            INSERT INTO document_pages (document_id, page_no, raw_text, cleaned_text)
            VALUES (@DocumentId, @PageNo, @RawText, @CleanedText)
            """, pages, transaction);
        transaction.Commit();
        _logger.LogInformation("Replaced {PageCount} pages in {ElapsedMs}ms", pages.Count, sw.ElapsedMilliseconds);
    }

    public async Task<IReadOnlyList<DocumentPageRecord>> GetPagesAsync(string documentId)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = documentId });
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<DocumentPageRecord>(
            "SELECT * FROM document_pages WHERE document_id = @documentId ORDER BY page_no", new { documentId });
        var list = rows.AsList();
        _logger.LogDebug("Got {Count} pages in {ElapsedMs}ms", list.Count, sw.ElapsedMilliseconds);
        return list;
    }

    public async Task ReplaceChunksAsync(string documentId, IReadOnlyList<ChunkRecord> chunks)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = documentId });
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM chunks WHERE document_id = @documentId", new { documentId }, transaction);
        await connection.ExecuteAsync(
            """
            INSERT INTO chunks (document_id, seq, page_from, page_to, heading, content, char_count, created_at)
            VALUES (@DocumentId, @Seq, @PageFrom, @PageTo, @Heading, @Content, @CharCount, @CreatedAt)
            """, chunks, transaction);
        transaction.Commit();
        _logger.LogInformation("Replaced {ChunkCount} chunks in {ElapsedMs}ms", chunks.Count, sw.ElapsedMilliseconds);
    }

    public async Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(string documentId)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = documentId });
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<ChunkRecord>(
            "SELECT * FROM chunks WHERE document_id = @documentId ORDER BY seq", new { documentId });
        var list = rows.AsList();
        _logger.LogDebug("Got {Count} chunks in {ElapsedMs}ms", list.Count, sw.ElapsedMilliseconds);
        return list;
    }

    public async Task<IReadOnlyList<ChunkRecord>> GetChunksByIdsAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
            return [];
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var rows = await connection.QueryAsync<ChunkRecord>(
            "SELECT * FROM chunks WHERE id IN @ids ORDER BY seq", new { ids });
        var list = rows.AsList();
        _logger.LogDebug("Got {Count} chunks by ids in {ElapsedMs}ms", list.Count, sw.ElapsedMilliseconds);
        return list;
    }

    public async Task<int> CountChunksAsync(string documentId)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = documentId });
        var sw = Stopwatch.StartNew();
        using var connection = _factory.Create();
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(1) FROM chunks WHERE document_id = @documentId", new { documentId });
        _logger.LogDebug("Count chunks: {Count} in {ElapsedMs}ms", count, sw.ElapsedMilliseconds);
        return count;
    }
}
