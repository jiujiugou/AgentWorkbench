using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Knowledge.Local.Embedding;

/// <summary>按配置选择 Embedding 实现（none 时降级为空实现），并统一套一层可靠性装饰器。</summary>
/// <remarks>
/// 选择实现与包装饰放在同一处：拆成两处会让"某个实现忘了包"成为可能。
/// 所有实现（含 Null）都走同一条路径，失败归因与超时/重试因此没有例外。
/// </remarks>
public sealed class EmbeddingProviderRegistry : IEmbeddingProvider
{
    private readonly ResilientEmbeddingProvider _resilient;

    public EmbeddingProviderRegistry(
        IOptions<KnowledgeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<OllamaEmbeddingProvider> ollamaLogger,
        ILogger<OpenAiEmbeddingProvider> openAiLogger,
        ILogger<ResilientEmbeddingProvider> resilientLogger)
    {
        var embedding = options.Value.Embedding;
        var concrete = embedding.Provider?.Trim().ToLowerInvariant() switch
        {
            "ollama" => (IEmbeddingProvider)new OllamaEmbeddingProvider(options, ollamaLogger),
            "openai" => new OpenAiEmbeddingProvider(options, httpClientFactory, openAiLogger),
            _ => new NullEmbeddingProvider(embedding.Dimensions)
        };

        _resilient = new ResilientEmbeddingProvider(concrete, resilientLogger, embedding);
    }

    public string Name => _resilient.Name;
    public string Model => _resilient.Model;

    /// <summary>透传内层；"已配置"不代表服务可用——真实可用性看 <see cref="LastFailure"/>。</summary>
    public bool IsConfigured => _resilient.IsConfigured;

    public int Dimensions => _resilient.Dimensions;

    /// <summary>最近一次失败的归因（健康检查与诊断用）；从未失败为 <c>null</c>。</summary>
    public EmbeddingFailure? LastFailure => _resilient.LastFailure;

    /// <summary>重试耗尽的累计轮数。</summary>
    public long FailureCount => _resilient.FailureCount;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        => _resilient.EmbedAsync(texts, cancellationToken);
}
