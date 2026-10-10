using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Shared.Options;

namespace AgentWorkbench.Knowledge.Local.Embedding;

/// <summary>按配置选择 Embedding 实现（none 时降级为空实现）。</summary>
public sealed class EmbeddingProviderRegistry : IEmbeddingProvider
{
    private readonly IEmbeddingProvider _inner;

    public EmbeddingProviderRegistry(
        IOptions<KnowledgeOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<OllamaEmbeddingProvider> ollamaLogger,
        ILogger<OpenAiEmbeddingProvider> openAiLogger)
    {
        var embedding = options.Value.Embedding;
        _inner = embedding.Provider?.Trim().ToLowerInvariant() switch
        {
            "ollama" => new OllamaEmbeddingProvider(options, ollamaLogger),
            "openai" => new OpenAiEmbeddingProvider(options, httpClientFactory, openAiLogger),
            _ => new NullEmbeddingProvider(embedding.Dimensions)
        };
    }

    public string Name => _inner.Name;
    public string Model => _inner.Model;
    public bool IsConfigured => _inner.IsConfigured;
    public int Dimensions => _inner.Dimensions;

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        => _inner.EmbedAsync(texts, cancellationToken);
}
