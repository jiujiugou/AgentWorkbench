namespace AgentWorkbench.Shared.Options;

/// <summary>知识库（RAG）配置：引擎选择、检索参数、Embedding、重排。</summary>
public sealed class KnowledgeOptions
{
    public const string SectionName = "Knowledge";

    /// <summary>知识引擎：local | anythingllm | ragflow | opencontracts</summary>
    public string Provider { get; set; } = "local";

    /// <summary>返回给问答的片段数</summary>
    public int TopK { get; set; } = 5;

    /// <summary>每路召回（向量 / 全文）的候选数</summary>
    public int CandidateK { get; set; } = 20;

    /// <summary>融合分阈值：低于此值视为"资料不足"</summary>
    public double MinFusedScore { get; set; } = 0.0;

    /// <summary>向量路最大余弦距离（sqlite-vec 返回距离，越小越相似）：超过此值的候选丢弃，避免无关问题命中。</summary>
    public double MaxVectorDistance { get; set; } = 0.6;

    public EmbeddingOptions Embedding { get; set; } = new();

    public RerankOptions Rerank { get; set; } = new();
}

/// <summary>Embedding 配置：Provider = none | ollama | openai（OpenAI 兼容 /embeddings）。</summary>
public sealed class EmbeddingOptions
{
    public string Provider { get; set; } = "none";

    public string BaseUrl { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "bge-m3";

    public string ApiKey { get; set; } = string.Empty;

    public int Dimensions { get; set; } = 1024;
}

/// <summary>重排配置（P1 默认关闭）。</summary>
public sealed class RerankOptions
{
    public bool Enabled { get; set; }
}
