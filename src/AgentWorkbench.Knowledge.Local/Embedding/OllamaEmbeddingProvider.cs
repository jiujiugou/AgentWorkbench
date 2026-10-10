using System.Diagnostics;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace AgentWorkbench.Knowledge.Local.Embedding;

/// <summary>基于 Ollama（OllamaSharp）的本地向量化，默认模型 bge-m3。</summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ILogger<OllamaEmbeddingProvider> _logger;

    public OllamaEmbeddingProvider(IOptions<KnowledgeOptions> options, ILogger<OllamaEmbeddingProvider> logger)
    {
        var embedding = options.Value.Embedding;
        Model = string.IsNullOrWhiteSpace(embedding.Model) ? "bge-m3" : embedding.Model;
        Dimensions = embedding.Dimensions;
        _generator = new OllamaApiClient(new Uri(embedding.BaseUrl), Model);
        _logger = logger;
    }

    public string Name => "ollama";
    public string Model { get; }
    public bool IsConfigured => true;
    public int Dimensions { get; }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("EmbedAsync started: Model={Model} TextCount={Count}", Model, texts.Count);
        var generated = (await _generator.GenerateAsync(texts, cancellationToken: cancellationToken)).ToArray();
        var result = new float[generated.Length][];
        for (var i = 0; i < generated.Length; i++)
            result[i] = generated[i].Vector.ToArray();
        _logger.LogInformation("EmbedAsync completed: Model={Model} Count={Count} Dimensions={Dims} ElapsedMs={ElapsedMs}", Model, texts.Count, Dimensions, sw.ElapsedMilliseconds);
        return result;
    }
}
