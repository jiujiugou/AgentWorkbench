using Microsoft.EntityFrameworkCore;

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

    /// <summary>该文档已写入向量索引的块数（0 表示语义检索覆盖不到这篇）。</summary>
    Task<int> CountVectorsAsync(string documentId);
}

/// <summary>文档 / 页面 / 片段的持久化。</summary>
/// <remarks>
/// <para>EF Core 实现。唯一的例外是 <see cref="CountVectorsAsync"/>：<c>chunks_vec</c> 是
/// <c>CommunityToolkit.VectorData</c> 在首次向量化时自行建立的载荷表，EF 没有它的实体映射，
/// 因此走 <c>SqlQueryRaw</c>。它的列名是 C# 属性名（<c>DocumentId</c>，非蛇形），与其余表不同，
/// 这一点必须在裸 SQL 里原样写。</para>
/// <para><b>为什么 <c>Replace*</c> 必须显式事务</b>：它们都是「先删光再重插」。
/// 两条语句不在同一事务里时，中途失败会让文档变成「页数 0 篇但状态是 parsed」——
/// 这正是索引与正文对不上的经典成因。</para>
/// </remarks>
public sealed class DocumentRepository(
    IDbContextFactory<AppDbContext> contextFactory,
    ILogger<DocumentRepository> logger) : IDocumentRepository
{
    /// <summary>插入一条文档记录。</summary>
    /// <remarks>
    /// 唯一性由数据库裁决，**调用方不得「先查再插」**——并发下两个请求会同时查到「不存在」，
    /// 然后各插一条，两条记录共享同一磁盘文件，删一条连带毁另一条。
    /// 正确做法：直接插，捕获唯一冲突后转成「已存在」分支（不是错误）。
    /// <para>⚠️ 2026-10-30 现状：<c>documents.sha256</c> 只有普通索引 <c>ix_documents_sha256</c>，
    /// **唯一约束（M027）尚未应用**，所以上面这句话目前没有任何东西在守。</para>
    /// 出处：docs/设计/实现/知识资料管理.md §2、ADR 0006。
    /// </remarks>
    public async Task InsertAsync(DocumentRecord document)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = document.Id });

        await using var db = await contextFactory.CreateDbContextAsync();
        db.Documents.Add(document);
        await db.SaveChangesAsync();

        logger.LogInformation("Inserted document {FileName} ({SizeBytes} bytes)", document.FileName, document.SizeBytes);
    }

    /// <remarks>
    /// 原 SQL 用 <c>COALESCE(@pageCount, page_count)</c> / <c>COALESCE(@parsedAt, parsed_at)</c>：
    /// 传 <c>null</c> 表示「这两个字段不动」。EF 的 <c>ExecuteUpdate</c> 表达不了条件式赋值，
    /// 所以读改写；EF 只写回被修改的属性，不会把整行覆盖。
    /// </remarks>
    public async Task UpdateStatusAsync(string id, string status, string? error, int? pageCount, string? parsedAt)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = id });

        await using var db = await contextFactory.CreateDbContextAsync();
        var entity = await db.Documents.FirstOrDefaultAsync(d => d.Id == id);
        if (entity is null)
        {
            logger.LogWarning("Update document status: {DocumentId} not found", id);
            return;
        }

        entity.Status = status;
        entity.Error = error;
        if (pageCount is not null)
            entity.PageCount = pageCount.Value;
        if (parsedAt is not null)
            entity.ParsedAt = parsedAt;

        await db.SaveChangesAsync();
        logger.LogInformation("Updated document status to {Status} (pages={PageCount})", status, pageCount);
    }

    public async Task<DocumentRecord?> GetAsync(string id)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
    }

    public async Task<IReadOnlyList<DocumentRecord>> ListAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Documents.AsNoTracking().OrderByDescending(d => d.CreatedAt).ToListAsync();
    }

    /// <summary>删除文档记录（级联页与片段）。</summary>
    /// <remarks>
    /// **只允许文档生命周期服务调用**（设计判据 17）。它只是删除三层承诺里的 L1，
    /// 调用方必须已经按顺序做过：取片段 id 清单 → 本方法（事务内）→ 删向量（L2）→ 删文件（L3）。
    /// 顺序不能换：先删向量再删记录而删记录失败，文档还在列表里却已经搜不到，用户会认为「检索坏了」。
    /// <para>⚠️ 现状：生命周期服务尚不存在，本方法被 Controller 直接调用，L2/L3 也没有清理。</para>
    /// <para><b>级联依赖连接串的 <c>ForeignKeys=True</c></b>：<c>document_pages</c> / <c>chunks</c>
    /// 的级联删除由 SQLite 外键执行，EF 自己不会发第二条 DELETE。连接串漏了这个参数，
    /// 这里就会静默留下孤儿行。断言见 <c>SqliteForeignKeysTests</c>。</para>
    /// 出处：docs/设计/实现/知识资料管理.md §5、ADR 0005。
    /// </remarks>
    public async Task<bool> DeleteAsync(string id)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = id });

        await using var db = await contextFactory.CreateDbContextAsync();
        var deleted = await db.Documents.Where(d => d.Id == id).ExecuteDeleteAsync();

        logger.LogInformation("Delete document: {Deleted}", deleted > 0);
        return deleted > 0;
    }

    public async Task ReplacePagesAsync(string documentId, IReadOnlyList<DocumentPageRecord> pages)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = documentId });

        await using var db = await contextFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.DocumentPages.Where(p => p.DocumentId == documentId).ExecuteDeleteAsync();
        db.DocumentPages.AddRange(pages);
        await db.SaveChangesAsync();

        await transaction.CommitAsync();
        logger.LogInformation("Replaced {PageCount} pages", pages.Count);
    }

    public async Task<IReadOnlyList<DocumentPageRecord>> GetPagesAsync(string documentId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.DocumentPages
            .AsNoTracking()
            .Where(p => p.DocumentId == documentId)
            .OrderBy(p => p.PageNo)
            .ToListAsync();
    }

    /// <remarks>
    /// <b>写完这一批之后 chunk 的自增 id 才有值</b>（<c>SaveChangesAsync</c> 回填）。
    /// 下游的全文索引与向量索引都拿这个 id 做键，所以顺序是先 ReplaceChunks、再 IndexDocument——
    /// 调用方不要调换。
    /// </remarks>
    public async Task ReplaceChunksAsync(string documentId, IReadOnlyList<ChunkRecord> chunks)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["DocumentId"] = documentId });

        await using var db = await contextFactory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        await db.Chunks.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync();
        db.Chunks.AddRange(chunks);
        await db.SaveChangesAsync();

        await transaction.CommitAsync();
        logger.LogInformation("Replaced {ChunkCount} chunks", chunks.Count);
    }

    public async Task<IReadOnlyList<ChunkRecord>> GetChunksAsync(string documentId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Chunks
            .AsNoTracking()
            .Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.Seq)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ChunkRecord>> GetChunksByIdsAsync(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
            return [];

        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Chunks
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.Seq)
            .ToListAsync();
    }

    public async Task<int> CountChunksAsync(string documentId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.Chunks.CountAsync(c => c.DocumentId == documentId);
    }

    /// <remarks>
    /// <c>chunks_vec</c> 只在首次向量化成功时由向量库自动建立：空库或从未跑通时这张表不存在，
    /// 语义上等价于「0 条向量」，不该让整个列表接口 500。
    /// </remarks>
    public async Task<int> CountVectorsAsync(string documentId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        try
        {
            return await db.Database
                .SqlQueryRaw<int>("SELECT COUNT(1) AS \"Value\" FROM chunks_vec WHERE DocumentId = {0}", documentId)
                .SingleAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("chunks_vec 尚未建立，按 0 条向量处理。");
            return 0;
        }
    }
}
