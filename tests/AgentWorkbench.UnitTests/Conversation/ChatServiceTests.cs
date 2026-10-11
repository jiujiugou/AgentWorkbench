using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkbench.Conversation;
using AgentWorkbench.Domain;
using AgentWorkbench.Providers;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace AgentWorkbench.UnitTests.Conversation;

public sealed class ChatServiceTests
{
    private readonly Mock<IChatClientFactory> _factoryMock;
    private readonly IOptions<ProviderOptions> _options;
    private readonly Mock<ILogger<ChatService>> _loggerMock;
    private readonly StubChatClient _stubClient;
    private readonly ChatService _service;
    private readonly ITestOutputHelper _output;

    public ChatServiceTests(ITestOutputHelper output)
    {
        _output = output;
        _factoryMock = new Mock<IChatClientFactory>(MockBehavior.Strict);
        _options = Options.Create(new ProviderOptions
        {
            SystemPrompt = "Default system prompt",
            Temperature = 0.5f
        });
        _loggerMock = new Mock<ILogger<ChatService>>();
        _stubClient = new StubChatClient();

        _factoryMock.Setup(f => f.GetClient(It.IsAny<ChatEndpoint>()))
            .Returns(_stubClient);

        _service = new ChatService(
            _factoryMock.Object, new PassthroughModelRuntime(), _options, new FakeToolCatalog(), _loggerMock.Object);
    }

    private ChatEndpoint SetupResolved() =>
        new(1, "http://test", "key", "model");

    [Fact]
    public async Task StreamAsync_Throws_WhenNotConfigured()
    {
        _factoryMock.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatEndpoint?)null);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _service.StreamAsync([], "hello", CancellationToken.None).FirstAsync();
        });
    }

    [Fact]
    public async Task StreamAsync_MapsHistoryRoles_AndAppendsUserMessage()
    {
        _factoryMock.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SetupResolved());

        var history = new List<ChatTurn>
        {
            new("user", "hello"),
            new("assistant", "hi there"),
            new("user", "how are you")
        };

        await foreach (var _ in _service.StreamAsync(history, "final question", CancellationToken.None))
        {
        }

        var captured = _stubClient.CapturedMessages;
        Assert.NotEmpty(captured);

        var last = captured[^1];
        Assert.Equal(ChatRole.User, last.Role);
        Assert.Equal("final question", GetText(last));

        Assert.Contains(captured, m => m.Role == ChatRole.User && GetText(m) == "hello");
        Assert.Contains(captured, m => m.Role == ChatRole.Assistant && GetText(m) == "hi there");
        Assert.Contains(captured, m => m.Role == ChatRole.User && GetText(m) == "how are you");
    }

    [Fact]
    public async Task StreamAsync_ForwardsTemperature()
    {
        _factoryMock.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SetupResolved());
        _stubClient.ScriptedUpdates = [new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])];

        await _service.StreamAsync([], "test", CancellationToken.None).FirstAsync();

        var opts = _stubClient.CapturedOptions;
        Assert.NotNull(opts);
        Assert.Equal(0.5f, opts.Temperature);
    }

    [Fact]
    public async Task StreamAsync_PassesToolsFromCatalog()
    {
        _factoryMock.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SetupResolved());

        _stubClient.ScriptedUpdates = [new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("ok")])];

        var tool = AIFunctionFactory.Create(() => "pong", "ping");
        var serviceWithTools = new ChatService(
            _factoryMock.Object,
            new PassthroughModelRuntime(),
            _options,
            new FakeToolCatalog([tool]),
            _loggerMock.Object);

        await serviceWithTools.StreamAsync([], "test", CancellationToken.None).FirstAsync();

        var opts = _stubClient.CapturedOptions;
        Assert.NotNull(opts);
        Assert.NotNull(opts.Tools);
        Assert.Contains(tool, opts.Tools!);
    }

    [Fact]
    public async Task StreamAsync_YieldsStreamedTextDeltas()
    {
        _factoryMock.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SetupResolved());

        _stubClient.ScriptedUpdates =
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("He")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("llo")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(" world")])
        ];

        var chunks = new List<string>();
        await foreach (var chunk in _service.StreamAsync([], "test", CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        Assert.Equal("Hello world", string.Concat(chunks));
    }

    [Fact]
    public async Task IsConfiguredAsync_ReflectsResolveAsync()
    {
        _factoryMock.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SetupResolved());
        Assert.True(await _service.IsConfiguredAsync());

        _factoryMock.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ChatEndpoint?)null);
        Assert.False(await _service.IsConfiguredAsync());
    }

    [Fact]
    public async Task DIAG_DumpCapturedMessages()
    {
        _factoryMock.Setup(f => f.ResolveAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SetupResolved());

        var history = new List<ChatTurn>
        {
            new("user", "hello"),
            new("assistant", "hi there")
        };

        await foreach (var chunk in _service.StreamAsync(history, "final question", CancellationToken.None))
        {
            _output.WriteLine($"CHUNK: '{chunk}'");
        }

        foreach (var m in _stubClient.CapturedMessages)
        {
            _output.WriteLine($"MSG role={m.Role} text='{GetText(m)}'");
        }

        var opts = _stubClient.CapturedOptions;
        _output.WriteLine($"OPTIONS temperature={opts?.Temperature} tools={opts?.Tools?.Count ?? -1}");
        foreach (var t in opts?.Tools ?? [])
        {
            _output.WriteLine($"TOOL name={t.Name} desc={t.Description}");
        }
    }

    private static string GetText(ChatMessage message) =>
        string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));
}

// Minimal IChatClient stub capturing calls and returning scripted streaming updates.
internal sealed class StubChatClient : IChatClient
{
    public List<ChatMessage> CapturedMessages { get; private set; } = new();
    public ChatOptions? CapturedOptions { get; private set; }
    public List<ChatResponseUpdate> ScriptedUpdates { get; set; } = new();

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CapturedMessages = messages.ToList();
        CapturedOptions = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CapturedMessages = messages.ToList();
        CapturedOptions = options;
        return ToStream(ScriptedUpdates);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToStream(List<ChatResponseUpdate> updates)
    {
        foreach (var update in updates)
        {
            yield return update;
        }

        await Task.CompletedTask;
    }
}
