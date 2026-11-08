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

    /// <summary>向量路最大余弦距离（sqlite-vec 返回距离，越小越相似）：超过此值的候选丢弃，避免无关问题命中。</summary>
    /// <remarks>
    /// 它只做<b>候选过滤</b>，不承担"够不够"的判定——两件事共用一个旋钮时，调它分不清是"候选少了"还是"闸严了"。
    /// 判定用的是下面两个可空阈值。
    /// </remarks>
    public double MaxVectorDistance { get; set; } = 0.6;

    /// <summary>
    /// 拒答阈值：第一名与第二到第五名平均距离之间的间隔低于它，判"资料不足"。
    /// </summary>
    /// <remarks>
    /// <c>null</c> 表示还没定标——此时永不判不足，结论落在"未判定"。不要用 0 代替：
    /// 0 的含义是"间隔小于零才拒"，几乎等于永不拒，看起来却像配置过。
    /// </remarks>
    public double? RefuseMargin { get; set; }

    /// <summary>
    /// 充分阈值：第一名距离低于它，才算"够近"，配合 <see cref="RefuseMargin"/> 一起才能判"已回答"。
    /// </summary>
    /// <remarks>
    /// 两个阈值必须同时在场。只配充分距离时，"挤在一起但恰好都很近"的命中会被判成已回答，
    /// 而那正是"没有赢家"的形状。未定标时同样留 <c>null</c>。
    /// </remarks>
    public double? SufficientDistance { get; set; }

    /// <summary>
    /// 引用二级校验的覆盖率下限（0 到 1）：该句的两字组合有多少出现在所指的那一块里。
    /// </summary>
    /// <remarks>
    /// <c>null</c> 表示还没量出合适的阈值——此时只记录覆盖率数值、不把引用判成"指向错块"。
    /// 不要用 0 代替：0 会让"没判定"和"判定通过"在结果里长得完全一样，
    /// 而这两件事的含义正相反。
    /// </remarks>
    public double? CitationCoverageThreshold { get; set; }

    /// <summary>
    /// 第二层「承认」的判定线：无出处句比例达到它，整段答案标"无资料支撑"。
    /// </summary>
    /// <remarks>
    /// 这一档（同主题但资料里没写）不该拒答——拒了会误伤真实问题——但必须让用户看得出"这段没有资料撑着"。
    /// <c>null</c> 表示未配置，只显示比例、不下结论。
    /// <para>
    /// <b>默认取 1.0，而不是留空</b>，与上面两个拒答阈值不同：那两个数要从距离分布里量出来，没量就没资格说话；
    /// 这个数数的是句子、不是距离，不依赖任何分布，可以直接落在最保守的端点——"一句出处都没有才标"。
    /// 留空的后果和第一层一样：代码全绿、界面永远不显示，等于没做。
    /// </para>
    /// <para>
    /// 与 <see cref="RefuseMargin"/> 不同，这里 <b>0 也是合法值</b>："一句没出处就要看见"是个真能被选中的策略。
    /// 拒答阈值配 0 等于永不拒，却看起来像配过了，所以那里禁止；这里不禁止。
    /// </para>
    /// </remarks>
    public double? UnsupportedRatio { get; set; } = 1.0;

    /// <summary>
    /// 一次检索送进模型的资料字符预算：五块 content 加起来不应超过它。
    /// </summary>
    /// <remarks>
    /// <c>null</c> 表示还没量出模型窗口的余量——此时只记录实际字符数、不告警。
    /// 不要用 0 代替：0 的含义是"一个字都不许送"，几乎等于每次告警，看起来却像配置过。
    /// <para>
    /// 这是「内容被截断」那条子问题唯一的观测口：不记这个数，就没人知道五块全文到底多大，
    /// 而截断不会报错——用户只看到答案少了一半。
    /// </para>
    /// </remarks>
    public int? ContextCharBudget { get; set; }

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
