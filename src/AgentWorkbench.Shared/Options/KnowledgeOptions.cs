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

    /// <summary>
    /// 一轮批量向量化的总预算（含全部重试与退避）。超时按暂时性失败处理。
    /// </summary>
    /// <remarks>
    /// 是"总寿命"而不是"单次超时"：单次超时由装饰器从总预算内派生（约 80% / (重试次数+1)）。
    /// 若把总预算当单次超时用，重试加退避必然超出它，RetryCount 会形同虚设。
    /// </remarks>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>暂时性失败的重试次数；0 = 不重试。永久性失败（401/404/维度不符）一律不重试。</summary>
    public int MaxRetryAttempts { get; set; } = 2;
}

/// <summary>重排配置（P1 默认关闭）。</summary>
public sealed class RerankOptions
{
    public bool Enabled { get; set; }
}
