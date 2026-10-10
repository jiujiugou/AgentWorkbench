using AgentWorkbench.Knowledge.Abstractions;
using Microsoft.Extensions.VectorData;

namespace AgentWorkbench.Knowledge.Local.Retrieval;

/// <summary>向量集合中的一条 chunk 记录（正文回查 chunks 表，不在此重复存）。</summary>
public sealed class ChunkVectorRecord
{
    [VectorStoreKey]
    public long ChunkId { get; set; }

    [VectorStoreData]
    public string DocumentId { get; set; } = string.Empty;

    [VectorStoreData]
    public int Seq { get; set; }

    [VectorStoreData]
    public string? Heading { get; set; }

    [VectorStoreData]
    public int PageFrom { get; set; }

    [VectorStoreData]
    public int PageTo { get; set; }

    [VectorStoreVector(
        KnowledgeConstants.EmbeddingDimensions,
        DistanceFunction = DistanceFunction.CosineDistance)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
