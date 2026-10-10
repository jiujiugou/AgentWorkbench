using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentWorkbench.Knowledge.Local.Embedding;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.UnitTests.Embedding;

public class OllamaEmbeddingProviderTests
{
    private static IOptions<KnowledgeOptions> CreateOptions(string? model, string baseUrl = "http://localhost:11434")
    {
        return Options.Create(new KnowledgeOptions
        {
            Embedding = new EmbeddingOptions
            {
                Provider = "ollama",
                Model = model!,
                BaseUrl = baseUrl,
                Dimensions = 1024
            }
        });
    }

    private static OllamaEmbeddingProvider Create(string? model, string baseUrl = "http://localhost:11434")
    {
        var logger = new Mock<ILogger<OllamaEmbeddingProvider>>().Object;
        return new OllamaEmbeddingProvider(CreateOptions(model, baseUrl), logger);
    }

    [Fact]
    public void Constructor_NullModel_DefaultsToBgeM3()
    {
        var provider = Create(null);

        Assert.Equal("bge-m3", provider.Model);
    }

    [Fact]
    public void Constructor_EmptyModel_DefaultsToBgeM3()
    {
        var provider = Create("");

        Assert.Equal("bge-m3", provider.Model);
    }

    [Fact]
    public void Constructor_WhitespaceModel_DefaultsToBgeM3()
    {
        var provider = Create("   ");

        Assert.Equal("bge-m3", provider.Model);
    }

    [Fact]
    public void Constructor_CustomModel_KeepsModel()
    {
        var provider = Create("mxbai-embed-large");

        Assert.Equal("mxbai-embed-large", provider.Model);
    }

    [Fact]
    public void Constructor_SetsDimensionsAndIsConfigured()
    {
        var provider = Create("bge-m3");

        Assert.Equal(1024, provider.Dimensions);
        Assert.True(provider.IsConfigured);
    }

    [Fact]
    public async Task EmbedAsync_WithMockOllamaServer_ReturnsEmbeddings()
    {
        using var server = new MockOllamaServer();
        var provider = Create("bge-m3", server.BaseUrl);

        var result = await provider.EmbedAsync(new[] { "text1", "text2" }, CancellationToken.None);

        Assert.Equal(2, result.Length);
        Assert.Equal(new float[] { 1f, 2f }, result[0]);
        Assert.Equal(2, result[1].Length);
    }
}

/// <summary>极简本地 TCP mock，模拟 Ollama 的 /api/embed 端点。</summary>
internal sealed class MockOllamaServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }

    public string BaseUrl => $"http://127.0.0.1:{Port}/";

    public MockOllamaServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var received = new List<byte>();
                var buffer = new byte[8192];
                var headerEnd = -1;
                var contentLength = 0;

                while (true)
                {
                    if (headerEnd < 0)
                    {
                        var headerText = Encoding.UTF8.GetString(received.ToArray());
                        headerEnd = headerText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (headerEnd >= 0)
                        {
                            foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                            {
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    contentLength = int.Parse(line["Content-Length:".Length..].Trim());
                            }
                        }
                    }

                    if (headerEnd >= 0 && received.Count - (headerEnd + 4) >= contentLength)
                        break;

                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read == 0)
                        break;

                    for (var i = 0; i < read; i++)
                        received.Add(buffer[i]);
                }

                var body = string.Empty;
                if (headerEnd >= 0)
                {
                    var bodyStart = headerEnd + 4;
                    var available = Math.Min(contentLength, received.Count - bodyStart);
                    if (available > 0)
                        body = Encoding.UTF8.GetString(received.Skip(bodyStart).Take(available).ToArray());
                }

                var responseBody = Encoding.UTF8.GetBytes(BuildResponse(body));
                var responseHeader =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json\r\n" +
                    $"Content-Length: {responseBody.Length}\r\n" +
                    "Connection: close\r\n" +
                    "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(responseHeader), _cts.Token);
                await stream.WriteAsync(responseBody, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
            catch
            {
                // 客户端提前断开等情况，忽略
            }
        }
    }

    private static string BuildResponse(string requestBody)
    {
        var count = 1;
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.TryGetProperty("input", out var input))
                count = input.ValueKind == JsonValueKind.Array ? input.GetArrayLength() : 1;
        }
        catch (JsonException)
        {
        }

        var vectors = new List<string>(count);
        for (var i = 0; i < count; i++)
            vectors.Add($"[{i + 1}.0,{i + 2}.0]");

        return "{\"model\":\"bge-m3\",\"embeddings\":[" + string.Join(",", vectors) + "]}";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
