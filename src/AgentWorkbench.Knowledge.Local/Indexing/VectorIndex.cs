using System.Diagnostics;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Local.Retrieval;
using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.VectorData;

namespace AgentWorkbench.Knowledge.Local.Indexing;

/// <summary>
/// 向量索引：封装 sqlite-vec（CommunityToolkit.VectorData.SqliteVec）的懒加载与错误降级。
/// 后端不可用时 <see cref="Enabled"/> 置 false，上层自动退回纯全文检索。
/// </summary>
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

    public async Task<List<VectorSearchResult<ChunkVectorRecord>>> SearchAsync(
        float[] vector, int top, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var collection = await GetCollectionAsync(create: false, cancellationToken);
        if (collection is null)
        {
            _logger.LogDebug("SearchAsync skipped: collection null ElapsedMs={ElapsedMs}", sw.ElapsedMilliseconds);
            return [];
        }

        var results = new List<VectorSearchResult<ChunkVectorRecord>>();
        await foreach (var result in collection.SearchAsync(
                           new ReadOnlyMemory<float>(vector), top, cancellationToken: cancellationToken))
        {
            results.Add(result);
        }
        _logger.LogDebug("SearchAsync: {Count} results ElapsedMs={ElapsedMs}", results.Count, sw.ElapsedMilliseconds);
        return results;
    }
}
