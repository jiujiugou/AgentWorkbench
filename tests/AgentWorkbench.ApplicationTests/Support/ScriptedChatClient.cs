using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.ApplicationTests.Support;

/// <summary>
/// 脚本化模型客户端：按轮次返回预设响应，记录每次请求的消息与调用次数。
/// 只用于测试；生产实现不参与。
/// </summary>
/// <remarks>
/// 取代原先散落在 ToolResultVetoTests 里的私有两轮脚本类。
/// 五项测试里凡是需要「模型」的地方都用这一个装置，避免每个测试类各写一份假客户端。
/// </remarks>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new();
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];
    private int _callCount;

    /// <summary>模型被请求的次数。</summary>
    public int CallCount => _callCount;

    /// <summary>每次请求收到的消息快照，用于断言「指令有没有进历史」「片段有没有注入」。</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => _requests;

    public IReadOnlyList<ChatMessage> LastRequest => _requests.Count == 0
        ? Array.Empty<ChatMessage>()
        : _requests[^1];

    /// <summary>脚本用尽后的兜底文本；默认给一段文本，避免测试里出现永不结束的函数环。</summary>
    public string FallbackText { get; set; } = "脚本用尽";

    /// <summary>为 true 时重复最后一个脚本步骤而不消耗队列（模拟「模型一直想调工具」）。</summary>
    public bool RepeatLast { get; set; }

    public ScriptedChatClient Reply(string text)
    {
        _steps.Enqueue(_ => Task.FromResult(Text(text)));
        return this;
    }

    public ScriptedChatClient ToolCall(
        string name,
        IDictionary<string, object?>? arguments = null,
        string callId = "call_1")
    {
        _steps.Enqueue(_ =>
        {
            var content = new FunctionCallContent(
                callId, name, arguments ?? new Dictionary<string, object?>());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [content])));
        });
        return this;
    }

    public ScriptedChatClient Fail(Exception exception)
    {
        _steps.Enqueue(_ => Task.FromException<ChatResponse>(exception));
        return this;
    }

    public ScriptedChatClient Delay(TimeSpan duration, string thenText = "迟到的答复")
    {
        _steps.Enqueue(async ct =>
        {
            await Task.Delay(duration, ct);
            return Text(thenText);
        });
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _callCount++;
        _requests.Add(messages.ToList());

        if (_steps.Count == 0)
            return Task.FromResult(Text(FallbackText));

        var step = RepeatLast ? _steps.Last() : _steps.Dequeue();
        return step(cancellationToken);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static ChatResponse Text(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));
}
