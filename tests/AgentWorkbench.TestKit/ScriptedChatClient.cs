using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.TestKit;

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

    /// <summary>最后一次请求收到的选项快照，用于断言指令是不是随 <c>ChatOptions.Instructions</c> 下发。</summary>
    public ChatOptions? LastOptions { get; private set; }

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

    /// <summary>
    /// 模拟流式分片：同一次调用拆成两片，首片带名字、后续片只带 CallId（名字为空）。
    /// 用来验「整轮结束再判」不会把一次调用记成两笔。
    /// </summary>
    public ScriptedChatClient ToolCallFragmented(string name, string callId = "call_1")
    {
        _steps.Enqueue(_ => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent(callId, name, new Dictionary<string, object?>()),
            new FunctionCallContent(callId, string.Empty, new Dictionary<string, object?>())
        ]))));
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
        LastOptions = options;

        if (_steps.Count == 0)
            return Task.FromResult(Text(FallbackText));

        var step = RepeatLast ? _steps.Last() : _steps.Dequeue();
        return step(cancellationToken);
    }

    /// <summary>
    /// 流式取一步脚本并按内容分片发出。<b>与 <see cref="GetResponseAsync"/> 共用同一个脚本队列</b>，
    /// 所以一个脚本客户端只能用其中一条路径。
    /// </summary>
    /// <remarks>
    /// 分片形状照 MEA 的约定：名字只在首片出现，后续片只带参数增量。
    /// 这正是 <c>ToolCallGuardChatClient</c> 需要「整轮结束再判」的原因——
    /// 在流程中间判会拿到不完整的调用。
    /// </remarks>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _callCount++;
        _requests.Add(messages.ToList());
        LastOptions = options;

        var response = _steps.Count == 0
            ? Text(FallbackText)
            : await (RepeatLast ? _steps.Last() : _steps.Dequeue())(cancellationToken);

        foreach (var message in response.Messages)
            foreach (var content in message.Contents)
                yield return new ChatResponseUpdate(message.Role, [content]);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static ChatResponse Text(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));
}
