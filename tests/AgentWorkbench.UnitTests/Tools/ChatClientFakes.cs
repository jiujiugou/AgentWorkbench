using Microsoft.Extensions.AI;

namespace AgentWorkbench.UnitTests.Tools;

/// <summary>
/// 脚本化模型客户端：按请求序号返回预设响应（工具调用 / 文本），
/// 用来在测试里驱动真实的函数调用环（FunctionInvokingChatClient），而不是绕过它。
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
        => throw new NotSupportedException("工具环用例只走非流式路径");

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
