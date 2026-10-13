using AgentWorkbench.ModelRuntime;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.EvaluationTests.Fixtures;

/// <summary>
/// 脚本化模型客户端：按请求序号返回预设响应（工具调用 / 文本），
/// 用来驱动真实的函数调用环（FunctionInvokingChatClient），而不是绕过它。
/// 与 UnitTests 的同名替身同形；这里是评测项目的自有副本（测试项目不互相引用）。
/// </summary>
internal sealed class ScriptedChatClient(Func<int, ChatResponse> respond) : IChatClient
{
    /// <summary>已发生的模型请求次数（序号从 1 起）。</summary>
    public int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(respond(Calls));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("评测只走非流式路径");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>构造脚本化响应的辅助方法。</summary>
internal static class ScriptedResponses
{
    private static int _callIds;

    /// <summary>一轮「模型要求调用某个工具」的响应。</summary>
    public static ChatResponse ToolCall(string toolName, IDictionary<string, object?> arguments)
        => new(new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent($"call-{Interlocked.Increment(ref _callIds)}", toolName, arguments)
        }));

    /// <summary>一轮「模型给出最终答复」的响应。</summary>
    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>按 (参数名, 值) 组装工具参数。</summary>
    public static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
        => pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
}

/// <summary>透传模型运行时：不叠加超时重试，评测只关心 harness 的编排。</summary>
internal sealed class PassthroughModelRuntime : IModelRuntime
{
    public IChatClient Wrap(IChatClient innerClient) => innerClient;

    public ModelUsageSnapshot GetUsage() => new(0, 0, 0);
}

/// <summary>
/// 进程级共享用量计量器——对应生产里的 <c>ModelUsageMeter</c> 单例：
/// 所有看到同一计量器的运行时，用量都汇入同一本账。P2 负控靠它复现串扰。
/// </summary>
internal sealed class UsageMeter
{
    private long _requests;
    private long _tokens;

    public void Record(long tokens)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Add(ref _tokens, tokens);
    }

    public ModelUsageSnapshot Snapshot()
        => new(Interlocked.Read(ref _requests), Interlocked.Read(ref _tokens), 0);
}

/// <summary>
/// 按请求累计用量的模型运行时替身：让 token 预算路径可在评测里被真实走到。
/// 可挂到共享 <see cref="UsageMeter"/> 上，模拟生产「进程级单例计量器」。
/// </summary>
internal sealed class MeteredModelRuntime(UsageMeter? meter = null) : IModelRuntime
{
    /// <summary>每次模型请求烧掉的 token 数。</summary>
    public long TokensPerRequest { get; init; }

    public IChatClient Wrap(IChatClient innerClient) => new MeteringChatClient(innerClient, this);

    public ModelUsageSnapshot GetUsage() => meter?.Snapshot() ?? new(0, 0, 0);

    private void Record() => meter?.Record(TokensPerRequest);

    private sealed class MeteringChatClient(IChatClient inner, MeteredModelRuntime owner) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await inner.GetResponseAsync(messages, options, cancellationToken);
            owner.Record();
            return response;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("评测只走非流式路径");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
