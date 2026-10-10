using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Knowledge.Local.Embedding;

/// <summary>OpenAI 兼容 /embeddings 供应商（如硅基流动 bge-m3）作为私有化之外的回退。</summary>
public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly ILogger<OpenAiEmbeddingProvider> _logger;

    public OpenAiEmbeddingProvider(IOptions<KnowledgeOptions> options, IHttpClientFactory httpClientFactory, ILogger<OpenAiEmbeddingProvider> logger)
    {
        var embedding = options.Value.Embedding;
        Model = embedding.Model;
        Dimensions = embedding.Dimensions;
        _apiKey = embedding.ApiKey;
        _endpoint = $"{embedding.BaseUrl.TrimEnd('/')}/embeddings";
        _http = httpClientFactory.CreateClient(nameof(OpenAiEmbeddingProvider));
        _logger = logger;
    }

    public string Name => "openai";
    public string Model { get; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && !string.IsNullOrWhiteSpace(Model);
    public int Dimensions { get; }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("EmbedAsync started: Model={Model} TextCount={Count}", Model, texts.Count);

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(new { model = Model, input = texts })
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Embedding 返回为空。");

        if (payload.Data is null || payload.Data.Length == 0)
            throw new InvalidOperationException("Embedding 返回数据为空。");

        var result = payload.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToArray();

        _logger.LogInformation("EmbedAsync completed: Model={Model} Count={Count} Dimensions={Dims} ElapsedMs={ElapsedMs}", Model, texts.Count, Dimensions, sw.ElapsedMilliseconds);
        return result;
    }

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] EmbeddingItem[] Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
