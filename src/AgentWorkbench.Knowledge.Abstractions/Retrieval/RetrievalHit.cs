namespace AgentWorkbench.Knowledge.Abstractions.Retrieval;

/// <summary>一条检索命中：来源 + 正文 + 各路分数（用于调试面板与引用）。</summary>
public sealed record RetrievalHit
{
    public long ChunkId { get; init; }
    public string DocumentId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public int Seq { get; init; }
    public string? Heading { get; init; }
    public int PageFrom { get; init; }
    public int PageTo { get; init; }
    public string Content { get; init; } = string.Empty;

    /// <summary>余弦相似度（向量路，缺失为 null）</summary>
    public double? VectorScore { get; init; }

    /// <summary>BM25 分数（全文路，缺失为 null）</summary>
    public double? TextScore { get; init; }

    /// <summary>RRF 融合分</summary>
    public double FusedScore { get; init; }
}
