namespace AgentWorkbench.Shared.Options;

/// <summary>
/// 知识库配置：<b>只剩索引写入与引用判定</b>。检索执行已移到 Python MCP server，
/// 检索参数（TopK / 候选宽度 / 向量距离阈值 / 拒答阈值 / 上下文预算）随之删除——
/// 留着会让配置里出现「改了没反应」的旋钮，比没有更糟。
/// </summary>
/// <remarks>
/// 拒答判定现在只覆盖「零命中」一条（<c>McpKnowledgeVeto</c> 调 <c>SufficiencyGate</c>），
/// 因为它不依赖向量距离；原本基于距离间隔的 <c>RefuseMargin</c> / <c>SufficientDistance</c>
/// 在 Python server 提供距离之前没有输入，配置它们等于撒谎。
/// </remarks>
public sealed class KnowledgeOptions
{
    public const string SectionName = "Knowledge";

    /// <summary>知识引擎：local | anythingllm | ragflow | opencontracts。只影响索引写入实现。</summary>
    public string Provider { get; set; } = "local";

    /// <summary>引用二级校验的覆盖率下限（0 到 1）：该句的两字组合有多少出现在所指的那一块里。</summary>
    public double? CitationCoverageThreshold { get; set; }

    /// <summary>无出处句比例达到它，整段答案标「无资料支撑」。默认 1.0：一句出处都没有才标。</summary>
    public double? UnsupportedRatio { get; set; } = 1.0;

    /// <summary>索引写入用的 embedding 配置。检索侧不用它——Python server 走词面路。</summary>
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
