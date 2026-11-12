using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Runtime.Tools;

/// <summary>
/// 工具调用核对：模型响应里出现**清单外**的工具名时记一笔，然后原样放行。
/// </summary>
/// <remarks>
/// <b>为什么不拦。</b><see cref="FunctionInvokingChatClient"/> 的
/// <c>TerminateOnUnknownCalls</c> 默认是 <c>false</c>——它会把「工具找不到」作为一条函数结果
/// 回填给模型，让模型自己换路。中止整轮等于剥夺这次自我纠正的机会，成熟实现（MEA 默认、
/// Anthropic 的 <c>is_error</c> 约定）都不这么做。
/// <para>
/// <b>那这里补什么。</b>补的是回填以外没人做的事：那条回填发生在本模块的
/// <c>FunctionInvoker</c> 之外，不记下来，事后就查不到「模型曾经请求过一个不存在的工具」——
/// 而「谁 / 何时 / 看到什么」要能答。所以只做观察，不改语义：名字放行，笔留下。
/// </para>
/// <para>
/// <b>位置必须在</b> <see cref="FunctionInvokingChatClient"/> 之内、真实模型之外：
/// 那是唯一同时看得到「本次工具清单」（<see cref="ChatOptions.Tools"/>）和模型原始响应的位置。
/// </para>
/// </remarks>
internal sealed class ToolCallGuardChatClient : DelegatingChatClient
{
    private readonly Action<string> _onUnknown;

    /// <summary>
    /// 本次会话见过的工具清单。后续轮次 MEA 可能清空 <see cref="ChatOptions.Tools"/>
    /// （收口时逼模型给文本），沿用上一次的清单才不会把正常调用误报成未知。
    /// </summary>
    private IReadOnlySet<string>? _allowed;

    public ToolCallGuardChatClient(IChatClient innerClient, Action<string> onUnknown)
        : base(innerClient)
        => _onUnknown = onUnknown;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
            Report(call.Name, options);

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var seen = new List<string>();
        await foreach (var update in base
                           .GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            foreach (var call in update.Contents.OfType<FunctionCallContent>())
                if (call.Name is { Length: > 0 })
                    seen.Add(call.Name);
            yield return update;
        }

        // MEA 的流式约定：名字只在首片出现，后续片只带参数增量。所以这里收集所有非空名字、
        // 整轮结束再判一次——既不会在流程中间判到不完整的调用，也不会把一次调用记成两笔。
        //
        // 局限（如实标注）：这一步没有按 CallId 把名字分片拼起来。若某个实现把名字本身也拆片
        // （"not_" + "in_whitelist"），两段都会被当成独立名字，前半段会被误报。现在依赖的是
        // MEA 的上述约定，不是自己的拼接逻辑；换实现时要重验这条（S04_07 会红）。
        foreach (var name in seen)
            Report(name, options);
    }

    private void Report(string? name, ChatOptions? options)
    {
        if (string.IsNullOrEmpty(name)) return;

        if (options?.Tools is { Count: > 0 } tools)
            _allowed = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        if (_allowed is { } allowed && allowed.Contains(name)) return;

        _onUnknown(name);
    }
}
