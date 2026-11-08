using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;

namespace AgentWorkbench.Knowledge.Abstractions;

/// <summary>知识引擎能力位，前端据此决定按钮是否可用。</summary>
public sealed record KnowledgeCapabilities(bool Retrieve, bool Ask, bool Upload, bool Rerank);

/// <summary>索引覆盖度的取值（API 契约，前端按此渲染；判据是「块数 vs 向量数」而不是文档状态列）。</summary>
public static class IndexStates
{
    /// <summary>全文与向量都已写入。</summary>
    public const string Full = "full";

    /// <summary>只有全文索引，语义检索覆盖不到这篇。</summary>
    public const string TextOnly = "text_only";

    /// <summary>部分块有向量（通常是建索引中途失败）。</summary>
    public const string Partial = "partial";

    /// <summary>文档无可索引内容。</summary>
    public const string Empty = "empty";

    /// <summary>尚未解析完成。</summary>
    public const string Pending = "pending";

    /// <summary>解析或索引永久失败。</summary>
    public const string Failed = "failed";

    /// <summary>
    /// 由「块数 vs 向量数」判定覆盖度，而不是读文档状态列。
    /// </summary>
    /// <remarks>
    /// 判据取自实际数据：历史数据的向量缺口无需回填即可现形，也不会出现
    /// 「状态说已索引、数据却是空的」这种字段与事实打架的情形。
    /// 两个 Controller 共用此方法，避免同一语义在两处各写一份而漂移。
    /// </remarks>
    public static string Describe(string status, int chunkCount, int vectorCount)
    {
        if (status == DocumentStatus.Failed)
            return Failed;
        if (status != DocumentStatus.Parsed)
            return Pending;
        if (chunkCount == 0)
            return Empty;
        if (vectorCount >= chunkCount)
            return Full;
        return vectorCount == 0 ? TextOnly : Partial;
    }
}

/// <summary>
/// 一次索引清理的结果：删完之后索引里还剩多少。
/// </summary>
/// <remarks>
/// 判据是"再数一遍"，而不是"Delete 调用没抛异常"：<c>chunks_fts</c> 与向量表都没有外键约束，
/// 删不掉不会报错，只会留下检索能命中、正文已消失的幽灵，并占掉 top-K 名额。
/// </remarks>
public sealed record RemoveResult(int FtsRemaining, int VectorsRemaining, EmbeddingFailure? Failure)
{
    /// <summary>
    /// 可以安全删除记录了。向量残留只有在向量路可用时才有害——
    /// 扩展不可用时检索本身也读不到（<c>HybridRetriever</c> 先判 Enabled），残留无害。
    /// </summary>
    public bool Cleaned => Failure is null && FtsRemaining == 0;
}

/// <summary>
/// 一次索引写入的结果：三份存储（chunks / FTS / 向量）各自的落库量与失败归因。
/// </summary>
/// <remarks>
/// 存在的理由是"失败必须能回到调用方"：只打日志的失败无法让文档状态、列表字段、健康检查做出反应，
/// 于是"只建了全文"和"全部建成"在系统里长得一模一样。
/// </remarks>
public sealed record IndexResult(
    int ChunkCount,
    int FtsCount,
    int VectorCount,
    EmbeddingFailure? Failure)
{
    /// <summary>向量已全部写入（唯一可以认为"语义检索覆盖这篇文档"的状态）。</summary>
    public bool VectorComplete => Failure is null && ChunkCount > 0 && VectorCount == ChunkCount;

    /// <summary>尝试过向量化但失败了；与"未启用向量"（Failure 为 null）不同。</summary>
    public bool HasFailure => Failure is not null;
}


/// <summary>知识库健康状态。</summary>
public sealed record KnowledgeStatus(
    string Provider,
    KnowledgeCapabilities Capabilities,
    bool EmbeddingConfigured,
    string EmbeddingProvider,
    string EmbeddingModel,
    int DocumentCount,
    int IndexedChunkCount);

/// <summary>
/// 可替换知识引擎统一接口（防腐层）：本地自研实现与将来接入的任何第三方引擎走同一契约。
/// </summary>
public interface IKnowledgeEngine
{
    string Provider { get; }

    KnowledgeCapabilities Capabilities { get; }

    Task<KnowledgeStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<IndexResult> IndexDocumentAsync(string documentId, CancellationToken cancellationToken);

    Task<RemoveResult> RemoveDocumentAsync(string documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken);
}
