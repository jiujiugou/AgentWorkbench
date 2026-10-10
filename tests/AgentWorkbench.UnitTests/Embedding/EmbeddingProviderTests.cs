using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Local.Embedding;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RichardSzalay.MockHttp;
using Xunit;

namespace AgentWorkbench.UnitTests.Embedding;

public class EmbeddingProviderRegistryTests
{
    private static IOptions<KnowledgeOptions> CreateOptions(string provider, string model = "bge-m3", string baseUrl = "http://localhost:11434", string apiKey = "test-key")
    {
        return Options.Create(new KnowledgeOptions
        {
            Embedding = new EmbeddingOptions
            {
                Provider = provider,
                Model = model,
                BaseUrl = baseUrl,
                ApiKey = apiKey,
                Dimensions = 1024
            }
        });
    }

    [Fact]
    public void Constructor_WithOllamaProvider_CreatesOllamaEmbeddingProvider()
    {
        var options = CreateOptions("ollama");
        var httpFactory = new Mock<IHttpClientFactory>().Object;
        var ollamaLogger = new Mock<ILogger<OllamaEmbeddingProvider>>().Object;
        var openAiLogger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var registry = new EmbeddingProviderRegistry(options, httpFactory, ollamaLogger, openAiLogger);

        Assert.Equal("ollama", registry.Name);
        Assert.Equal("bge-m3", registry.Model);
        Assert.True(registry.IsConfigured);
    }

    [Fact]
    public void Constructor_WithOpenAIProvider_CreatesOpenAiEmbeddingProvider()
    {
        var options = CreateOptions("openai");
        var httpFactory = new Mock<IHttpClientFactory>().Object;
        var ollamaLogger = new Mock<ILogger<OllamaEmbeddingProvider>>().Object;
        var openAiLogger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var registry = new EmbeddingProviderRegistry(options, httpFactory, ollamaLogger, openAiLogger);

        Assert.Equal("openai", registry.Name);
        Assert.Equal("bge-m3", registry.Model);
        Assert.True(registry.IsConfigured);
    }

    [Fact]
    public void Constructor_WithUnknownProvider_CreatesNullEmbeddingProvider()
    {
        var options = CreateOptions("unknown");
        var httpFactory = new Mock<IHttpClientFactory>().Object;
        var ollamaLogger = new Mock<ILogger<OllamaEmbeddingProvider>>().Object;
        var openAiLogger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var registry = new EmbeddingProviderRegistry(options, httpFactory, ollamaLogger, openAiLogger);

        Assert.Equal("none", registry.Name);
        Assert.False(registry.IsConfigured);
    }

    [Fact]
    public void Constructor_WithNoneProvider_CreatesNullEmbeddingProvider()
    {
        var options = CreateOptions("none");
        var httpFactory = new Mock<IHttpClientFactory>().Object;
        var ollamaLogger = new Mock<ILogger<OllamaEmbeddingProvider>>().Object;
        var openAiLogger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var registry = new EmbeddingProviderRegistry(options, httpFactory, ollamaLogger, openAiLogger);

        Assert.Equal("none", registry.Name);
        Assert.False(registry.IsConfigured);
    }

    [Fact]
    public void Constructor_CaseInsensitiveProviderMatching()
    {
        var options = CreateOptions("OLLAMA");
        var httpFactory = new Mock<IHttpClientFactory>().Object;
        var ollamaLogger = new Mock<ILogger<OllamaEmbeddingProvider>>().Object;
        var openAiLogger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var registry = new EmbeddingProviderRegistry(options, httpFactory, ollamaLogger, openAiLogger);

        Assert.Equal("ollama", registry.Name);
    }

    [Fact]
    public void Constructor_WithWhitespaceProvider_TrimsCorrectly()
    {
        var options = CreateOptions("  openai  ");
        var httpFactory = new Mock<IHttpClientFactory>().Object;
        var ollamaLogger = new Mock<ILogger<OllamaEmbeddingProvider>>().Object;
        var openAiLogger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var registry = new EmbeddingProviderRegistry(options, httpFactory, ollamaLogger, openAiLogger);

        Assert.Equal("openai", registry.Name);
    }
}

public class OpenAiEmbeddingProviderTests
{
    private static KnowledgeOptions CreateKnowledgeOptions(string baseUrl = "http://localhost:8000", string model = "bge-m3", string apiKey = "test-key")
    {
        return new KnowledgeOptions
        {
            Embedding = new EmbeddingOptions
            {
                Provider = "openai",
                BaseUrl = baseUrl,
                Model = model,
                ApiKey = apiKey,
                Dimensions = 1024
            }
        };
    }

    private static IOptions<KnowledgeOptions> CreateOptions(string baseUrl = "http://localhost:8000", string model = "bge-m3", string apiKey = "test-key")
    {
        return Options.Create(CreateKnowledgeOptions(baseUrl, model, apiKey));
    }

    private static MockHttpMessageHandler CreateMockHttpHandler()
    {
        return new MockHttpMessageHandler();
    }

    [Fact]
    public async Task EmbedAsync_Success_ReturnsEmbeddings()
    {
        var mockHttp = CreateMockHttpHandler();
        mockHttp.When(HttpMethod.Post, "http://localhost:8000/embeddings")
                .Respond(HttpStatusCode.OK, JsonContent.Create(new
                {
                    data = new[]
                    {
                        new { index = 0, embedding = new float[] { 0.1f, 0.2f, 0.3f } },
                        new { index = 1, embedding = new float[] { 0.4f, 0.5f, 0.6f } }
                    }
                }));

        var httpClient = mockHttp.ToHttpClient();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(nameof(OpenAiEmbeddingProvider))).Returns(httpClient);

        var options = CreateOptions();
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory.Object, logger);

        var result = await provider.EmbedAsync(new[] { "text1", "text2" }, CancellationToken.None);

        Assert.Equal(2, result.Length);
        Assert.Equal(3, result[0].Length);
        Assert.Equal(3, result[1].Length);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f }, result[0]);
        Assert.Equal(new float[] { 0.4f, 0.5f, 0.6f }, result[1]);
    }

    [Fact]
    public async Task EmbedAsync_ReturnsOrderedByIndex()
    {
        var mockHttp = CreateMockHttpHandler();
        mockHttp.When(HttpMethod.Post, "http://localhost:8000/embeddings")
                .Respond(HttpStatusCode.OK, JsonContent.Create(new
                {
                    data = new[]
                    {
                        new { index = 1, embedding = new float[] { 0.4f, 0.5f } },
                        new { index = 0, embedding = new float[] { 0.1f, 0.2f } }
                    }
                }));

        var httpClient = mockHttp.ToHttpClient();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(nameof(OpenAiEmbeddingProvider))).Returns(httpClient);

        var options = CreateOptions();
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory.Object, logger);

        var result = await provider.EmbedAsync(new[] { "text1", "text2" }, CancellationToken.None);

        Assert.Equal(new float[] { 0.1f, 0.2f }, result[0]);
        Assert.Equal(new float[] { 0.4f, 0.5f }, result[1]);
    }

    [Fact]
    public async Task EmbedAsync_HttpError_ThrowsInvalidOperationException()
    {
        var mockHttp = CreateMockHttpHandler();
        mockHttp.When(HttpMethod.Post, "http://localhost:8000/embeddings")
                .Respond(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("Unauthorized")
                });

        var httpClient = mockHttp.ToHttpClient();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(nameof(OpenAiEmbeddingProvider))).Returns(httpClient);

        var options = CreateOptions();
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory.Object, logger);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.EmbedAsync(new[] { "text1" }, CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_EmptyDataArray_ThrowsInvalidOperationException()
    {
        var mockHttp = CreateMockHttpHandler();
        mockHttp.When(HttpMethod.Post, "http://localhost:8000/embeddings")
                .Respond(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[]}")
                });

        var httpClient = mockHttp.ToHttpClient();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(nameof(OpenAiEmbeddingProvider))).Returns(httpClient);

        var options = CreateOptions();
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory.Object, logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EmbedAsync(new[] { "text1" }, CancellationToken.None));
        Assert.Contains("数据为空", ex.Message);
    }

    [Fact]
    public async Task EmbedAsync_NullJsonBody_ThrowsInvalidOperationException()
    {
        var mockHttp = CreateMockHttpHandler();
        mockHttp.When(HttpMethod.Post, "http://localhost:8000/embeddings")
                .Respond(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("null", Encoding.UTF8, "application/json")
                });

        var httpClient = mockHttp.ToHttpClient();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(nameof(OpenAiEmbeddingProvider))).Returns(httpClient);

        var options = CreateOptions();
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory.Object, logger);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EmbedAsync(new[] { "text1" }, CancellationToken.None));
        Assert.Contains("返回为空", ex.Message);
    }

    [Fact]
    public async Task EmbedAsync_NullData_ThrowsInvalidOperationException()
    {
        var mockHttp = CreateMockHttpHandler();
        mockHttp.When(HttpMethod.Post, "http://localhost:8000/embeddings")
                .Respond(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}")
                });

        var httpClient = mockHttp.ToHttpClient();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(nameof(OpenAiEmbeddingProvider))).Returns(httpClient);

        var options = CreateOptions();
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory.Object, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EmbedAsync(new[] { "text1" }, CancellationToken.None));
    }

    [Fact]
    public void IsConfigured_WithValidConfig_ReturnsTrue()
    {
        var options = CreateOptions();
        var httpFactory = new Mock<IHttpClientFactory>().Object;
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory, logger);

        Assert.True(provider.IsConfigured);
    }

    [Fact]
    public void IsConfigured_WithEmptyApiKey_ReturnsFalse()
    {
        var options = CreateOptions(apiKey: "");
        var httpFactory = new Mock<IHttpClientFactory>().Object;
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory, logger);

        Assert.False(provider.IsConfigured);
    }

    [Fact]
    public void IsConfigured_WithEmptyModel_ReturnsFalse()
    {
        var options = CreateOptions(model: "");
        var httpFactory = new Mock<IHttpClientFactory>().Object;
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory, logger);

        Assert.False(provider.IsConfigured);
    }

    [Fact]
    public async Task EmbedAsync_SendsCorrectRequest()
    {
        var mockHttp = CreateMockHttpHandler();
        string? capturedContent = null;
        string? capturedAuth = null;

        mockHttp.When(HttpMethod.Post, "http://localhost:8000/embeddings")
                .Respond(async req =>
                {
                    capturedContent = await req.Content!.ReadAsStringAsync();
                    if (req.Headers.TryGetValues("Authorization", out var authValues))
                        capturedAuth = authValues.FirstOrDefault();
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            data = new[]
                            {
                                new { index = 0, embedding = new float[] { 0.1f } }
                            }
                        })
                    };
                });

        var httpClient = mockHttp.ToHttpClient();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(nameof(OpenAiEmbeddingProvider))).Returns(httpClient);

        var options = CreateOptions();
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory.Object, logger);

        await provider.EmbedAsync(new[] { "hello world" }, CancellationToken.None);

        Assert.NotNull(capturedContent);
        var json = JsonDocument.Parse(capturedContent!);
        Assert.Equal("bge-m3", json.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello world", json.RootElement.GetProperty("input")[0].GetString());

        Assert.NotNull(capturedAuth);
        Assert.Contains("Bearer test-key", capturedAuth);
    }

    [Fact]
    public async Task EmbedAsync_CancellationToken_Respected()
    {
        var mockHttp = CreateMockHttpHandler();
        mockHttp.When(HttpMethod.Post, "http://localhost:8000/embeddings")
                .Respond(req => Task.Delay(1000).ContinueWith(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { data = new[] { new { index = 0, embedding = new float[] { 0.1f } } } })
                }));

        var httpClient = mockHttp.ToHttpClient();
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(f => f.CreateClient(nameof(OpenAiEmbeddingProvider))).Returns(httpClient);

        var options = CreateOptions();
        var logger = new Mock<ILogger<OpenAiEmbeddingProvider>>().Object;

        var provider = new OpenAiEmbeddingProvider(options, httpFactory.Object, logger);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.EmbedAsync(new[] { "text1" }, cts.Token));
    }
}

public class NullEmbeddingProviderTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var provider = new NullEmbeddingProvider(512);

        Assert.Equal("none", provider.Name);
        Assert.Equal(512, provider.Dimensions);
        Assert.False(provider.IsConfigured);
    }

    [Fact]
    public async Task EmbedAsync_AlwaysThrows()
    {
        var provider = new NullEmbeddingProvider(512);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.EmbedAsync(new[] { "text1" }, CancellationToken.None));
    }
}