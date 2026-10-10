namespace AgentWorkbench.Knowledge.Abstractions.Embedding;

/// <summary>文本向量化统一接口。</summary>
public interface IEmbeddingProvider
{
    /// <summary>实现名：ollama / openai / none</summary>
    string Name { get; }

    /// <summary>模型名</summary>
    string Model { get; }

    /// <summary>是否已配置（不代表服务可用）</summary>
    bool IsConfigured { get; }

    /// <summary>向量维度</summary>
    int Dimensions { get; }

    /// <summary>批量向量化，返回与输入等长的向量数组。</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>未配置时的空实现：调用即抛，供上层优雅降级为纯全文检索。</summary>
public sealed class NullEmbeddingProvider : IEmbeddingProvider
{
    public NullEmbeddingProvider(int dimensions = KnowledgeConstants.EmbeddingDimensions)
        => Dimensions = dimensions;

    public string Name => "none";
    public string Model => string.Empty;
    public bool IsConfigured => false;
    public int Dimensions { get; }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Embedding 未配置（Knowledge:Embedding:Provider = none）。");
}
