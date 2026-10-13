using System.Runtime.CompilerServices;
using AgentWorkbench.ModelRuntime;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.EvaluationTests.Fixtures;

/// <summary>
/// 脚本化模型客户端：按请求序号返回预设响应（工具调用 / 文本），
/// 用来驱动真实的函数调用环（FunctionInvokingChatClient），而不是绕过它。
/// 与 UnitTests 的同名替身同形；这里是评测项目的自有副本（测试项目不互相引用）。
/// </summary>
/// <remarks>
/// 同一个脚本同时供非流式与流式两条出口使用：流式把文本按 <see cref="ChunkChars"/> 切片成多个更新
/// （工具调用整块透传）——这样「增量累积」这条逻辑才会被真的测到，而不是收到一整段。
/// </remarks>
internal sealed class ScriptedChatClient(Func<int, ChatResponse> respond) : IChatClient
{
    /// <summary>流式文本切片长度：切得越小，流式路径的增量序列越接近真实 token 流。</summary>
    private const int ChunkChars = 4;

    /// <summary>已发生的模型请求次数（序号从 1 起）。</summary>
    public int Calls { get; private set; }

    /// <summary>每次模型请求实际收到的消息（按请求顺序）；用来断言「指令确实到了模型」，而不只是「调用了模型」。</summary>
    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(messages as IReadOnlyList<ChatMessage> ?? messages.ToList());
        return Task.FromResult(Respond());
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 请求序号与节流在两条出口上完全一致：流式不该因为「分片」而多算一次模型请求。
        var response = Respond();
        Requests.Add(messages as IReadOnlyList<ChatMessage> ?? messages.ToList());
        foreach (var update in Split(response))
            yield return update;

        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    private ChatResponse Respond()
    {
        Calls++;
        return respond(Calls);
    }

    /// <summary>把一整条响应拆成流式更新：文本切片，其余内容（函数调用等）整块透传。</summary>
    private static IEnumerable<ChatResponseUpdate> Split(ChatResponse response)
    {
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextContent { Text.Length: > 0 } text)
                {
                    for (var offset = 0; offset < text.Text.Length; offset += ChunkChars)
                        yield return new ChatResponseUpdate(message.Role,
                            [new TextContent(text.Text.Substring(offset, Math.Min(ChunkChars, text.Text.Length - offset)))]);
                    continue;
                }

                yield return new ChatResponseUpdate(message.Role, [content]);
            }
        }
    }
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
/// 抛固定异常的模型运行时替身：让「单次模型请求超时」这条路径可在评测里被走到。
/// 用途是把两套超时区分开——run 级墙钟（<c>AgentRunTimeoutException</c>）与
/// 单次模型请求超时（<c>TimeoutException</c>）：二者是继承关系，最容易在账本里混成一类。
/// </summary>
internal sealed class ThrowingModelRuntime(Exception exception) : IModelRuntime
{
    public IChatClient Wrap(IChatClient innerClient) => new ThrowingChatClient(exception);

    public ModelUsageSnapshot GetUsage() => new(0, 0, 0);

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ChatResponse>(exception);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("评测只走非流式路径");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
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
