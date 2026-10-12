using System.Text.Json;
using AgentWorkbench.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.UnitTests.Memory;

/// <summary>可手动推进的时钟：TTL / 过期用例要确定性，不能等真实时间。</summary>
internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// 会话工厂替身：<see cref="CreateAsync"/> 每次造一个新会话并计数，
/// <see cref="DeserializeAsync"/> 也计数——用来断言「到底是新建还是复用」。
/// </summary>
internal sealed class CountingSessionFactory : ISessionFactory
{
    // 只为拿到合法的 AgentSession 实例；本替身从不真正跑模型。
    private readonly AIAgent _agent = new ChatClientAgent(new NeverCalledChatClient(), new ChatClientAgentOptions
    {
        Name = "counting",
        UseProvidedChatClientAsIs = true,
        ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions())
    });

    private int _created;

    public int Created => _created;

    public int Deserialized { get; private set; }

    public async ValueTask<AgentSession> CreateAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _created);
        return await _agent.CreateSessionAsync(cancellationToken);
    }

    public ValueTask<JsonElement> SerializeAsync(AgentSession session, CancellationToken cancellationToken)
        => ValueTask.FromResult(JsonSerializer.Deserialize<JsonElement>("{}"));

    public async ValueTask<AgentSession> DeserializeAsync(JsonElement state, CancellationToken cancellationToken)
    {
        Deserialized++;
        return await _agent.CreateSessionAsync(cancellationToken);
    }

    private sealed class NeverCalledChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("会话身份用例不跑模型");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("会话身份用例不跑模型");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
