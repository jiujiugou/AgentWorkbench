namespace AgentWorkbench.Knowledge.Abstractions;

/// <summary>知识库常量。</summary>
public static class KnowledgeConstants
{
    /// <summary>向量维度（bge-m3 = 1024）。需与 Embedding 模型一致。</summary>
    public const int EmbeddingDimensions = 1024;

    /// <summary>sqlite-vec 向量集合名。</summary>
    public const string VectorCollection = "chunks_vec";

    /// <summary>FTS5 全文表名。</summary>
    public const string FtsTable = "chunks_fts";
}
