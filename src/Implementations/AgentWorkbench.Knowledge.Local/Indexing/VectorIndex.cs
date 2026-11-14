using System.Diagnostics;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Local.Retrieval;
using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

namespace AgentWorkbench.Knowledge.Local.Indexing;

/// <summary>
/// 向量索引：封装 sqlite-vec 的懒加载与错误降级。<b>只写不读</b>——检索已移到 Python MCP server。
/// </summary>
/// <remarks>
/// 后端不可用时 <see cref="Enabled"/> 置 false，写入侧自动退回「只建全文索引」。
/// 曾经这里有 <c>SearchAsync</c>（给 <c>HybridRetriever</c> 用），检索移出去后一并删除：
/// 留着一个没人调用的读取口，只会让人误以为 C# 还在参与召回。
/// </remarks>
public sealed class VectorIndex
{
    private readonly SqliteVectorStore _store;
    private readonly ILogger<VectorIndex> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private VectorStoreCollection<long, ChunkVectorRecord>? _collection;

    public VectorIndex(SqliteVectorStore store, ILogger<VectorIndex> logger)
    {
        _store = store;
        _logger = logger;
    }

    public bool Enabled { get; private set; } = true;

    public async Task<VectorStoreCollection<long, ChunkVectorRecord>?> GetCollectionAsync(
        bool create, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return null;
        if (_collection is not null)
            return _collection;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_collection is null)
            {
                var sw = Stopwatch.StartNew();
                var collection = _store.GetCollection<long, ChunkVectorRecord>(KnowledgeConstants.VectorCollection);
                if (create)
                    await collection.EnsureCollectionExistsAsync(cancellationToken);
                _collection = collection;
                _logger.LogInformation("VectorIndex initialized: Enabled={Enabled} ElapsedMs={ElapsedMs}", Enabled, sw.ElapsedMilliseconds);
            }
            return _collection;
        }
        catch (Exception ex)
        {
            Enabled = false;
            _logger.LogWarning(ex, "向量库初始化失败，降级为纯全文检索。");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertAsync(IReadOnlyList<ChunkVectorRecord> records, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var collection = await GetCollectionAsync(create: true, cancellationToken);
        if (collection is null)
        {
            _logger.LogDebug("UpsertAsync skipped: collection null ElapsedMs={ElapsedMs}", sw.ElapsedMilliseconds);
            return;
        }
        await collection.UpsertAsync(records, cancellationToken);
        _logger.LogDebug("UpsertAsync: {Count} records ElapsedMs={ElapsedMs}", records.Count, sw.ElapsedMilliseconds);
    }

    public async Task DeleteAsync(IEnumerable<long> chunkIds, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var collection = await GetCollectionAsync(create: false, cancellationToken);
        if (collection is null)
        {
            _logger.LogDebug("DeleteAsync skipped: collection null ElapsedMs={ElapsedMs}", sw.ElapsedMilliseconds);
            return;
        }
        await collection.DeleteAsync(chunkIds, cancellationToken);
        _logger.LogDebug("DeleteAsync: {Count} ids ElapsedMs={ElapsedMs}", chunkIds.Count(), sw.ElapsedMilliseconds);
    }
}
