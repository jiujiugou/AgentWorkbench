using System.ClientModel;
using System.Runtime.CompilerServices;
using System.Text;
using AgentWorkbench.Conversation;
using AgentWorkbench.Providers;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Conversation;

public sealed class ChatServiceToolFallbackTests
{
    [Fact]
    public async Task StreamAsync_RetriesWithoutTools_WhenClientRejectsToolRequest()
    {
        var client = new FallbackChatClient();
        var service = Build(client, supportsTools: true);

        var text = await Collect(service.StreamAsync([], "hi", CancellationToken.None));

        Assert.Equal("fallback-ok", text);
        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task StreamAsync_DoesNotPassTools_WhenSupportsToolsDisabled()
    {
        var client = new RecordingChatClient();
        var service = Build(client, supportsTools: false);

        var text = await Collect(service.StreamAsync([], "hi", CancellationToken.None));

        Assert.Equal("ok", text);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, client.LastToolCount);
    }

    private static ChatService Build(IChatClient client, bool supportsTools)
        => new(
            new FakeFactory(client),
            new PassthroughModelRuntime(),
            Options.Create(new ProviderOptions { SupportsTools = supportsTools, ApiKey = "k", Model = "m" }),
            new FakeToolCatalog([AIFunctionFactory.Create(() => "pong", "ping")]),
            NullLogger<ChatService>.Instance);

    private static async Task<string> Collect(IAsyncEnumerable<string> stream)
    {
        var builder = new StringBuilder();
        await foreach (var part in stream)
            builder.Append(part);
        return builder.ToString();
    }

    private sealed class FakeFactory(IChatClient client) : IChatClientFactory
    {
        public Task<ChatEndpoint?> ResolveAsync(string? modelOverride, CancellationToken cancellationToken)
            => Task.FromResult<ChatEndpoint?>(new ChatEndpoint(null, "http://localhost/v1", "k", "m"));

        public IChatClient GetClient(ChatEndpoint endpoint) => client;
    }

    /// <summary>第一次（带工具）调用抛 400，第二次（无工具）返回文本。</summary>
    private sealed class FallbackChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fallback-ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Calls == 1)
                throw new ClientResultException("Service request failed. Status: 400", null!, null!);

            yield return new ChatResponseUpdate(ChatRole.Assistant, "fallback-ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose() { }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public int LastToolCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            LastToolCount = options?.Tools?.Count ?? 0;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? key = null) => null;

        public void Dispose() { }
    }
}
