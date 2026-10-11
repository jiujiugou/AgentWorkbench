using AgentWorkbench.ModelRuntime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.ModelRuntime;

public sealed class ModelRuntimeTests
{
    [Fact]
    public void Wrap_IsIdempotent()
    {
        var runtime = Build(new ModelRuntimeOptions());
        var wrapped = runtime.Wrap(new StubChatClient(_ => Task.FromResult(Ok())));

        Assert.Same(wrapped, runtime.Wrap(wrapped));
    }

    [Fact]
    public async Task GetResponseAsync_RetriesTransientFailures_AndMetersUsage()
    {
        var attempts = 0;
        var runtime = Build(new ModelRuntimeOptions { MaxRetries = 2, RetryBaseDelayMilliseconds = 1 });
        var client = runtime.Wrap(new StubChatClient(_ =>
        {
            attempts++;
            return attempts < 3
                ? Task.FromException<ChatResponse>(new HttpRequestException("boom"))
                : Task.FromResult(Ok("ok"));
        }));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(3, attempts);
        Assert.Equal("ok", response.Text);
        Assert.Equal(3, runtime.GetUsage().Requests);
    }

    [Fact]
    public async Task GetResponseAsync_DoesNotRetry_NonTransientFailure()
    {
        var attempts = 0;
        var runtime = Build(new ModelRuntimeOptions { MaxRetries = 2, RetryBaseDelayMilliseconds = 1 });
        var client = runtime.Wrap(new StubChatClient(_ =>
        {
            attempts++;
            return Task.FromException<ChatResponse>(new InvalidOperationException("bad request"));
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task GetResponseAsync_ThrowsTimeout_WhenRequestExceedsBudget()
    {
        var runtime = Build(new ModelRuntimeOptions { RequestTimeoutSeconds = 1, MaxRetries = 0 });
        var client = runtime.Wrap(new StubChatClient(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Ok("late");
        }));

        await Assert.ThrowsAsync<TimeoutException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
    }

    private static ModelRuntimeService Build(ModelRuntimeOptions options)
        => new(
            Options.Create(options),
            new ModelUsageMeter(),
            NullLoggerFactory.Instance,
            NullLogger<ModelRuntimeService>.Instance);

    private static ChatResponse Ok(string text = "ok")
        => new(new ChatMessage(ChatRole.Assistant, text));

    private sealed class StubChatClient(Func<CancellationToken, Task<ChatResponse>> handler) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => handler(cancellationToken);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await handler(cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
