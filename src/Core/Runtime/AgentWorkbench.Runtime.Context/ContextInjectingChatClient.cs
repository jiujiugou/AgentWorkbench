using Microsoft.Extensions.AI;

namespace AgentWorkbench.Runtime.Context;

/// <summary>
/// 把上下文注入到模型请求的客户端装饰器：转发前把一条 system 消息插到最前。
/// </summary>
/// <remarks>
/// 与 MAF 的 <c>AIContextProviderChatClient</c> 做的是同一件事，但入口不同：
/// MAF 的 provider 需要 <c>AIAgent</c> / <c>AgentSession</c> 组成的 InvokingContext，
/// 而本项目把注入点放在「我们自己装配的函数调用环」之外、Agent 之内。
/// <para>
/// <b>为什么不在 Agent 上挂 <c>AIContextProviders</c></b>：那会让 MEA 在
/// <c>FunctionInvokingChatClient</c> 外面再包一层函数调用环，<c>ToolExecutionPipeline</c> 的
/// 守卫与轨迹采集被绕过（实测：同参重复调用不再判 <c>loop_detected</c>，工具异常不再归类 <c>tool_error</c>）。
/// </para>
/// <para>
/// 注入进的是<b>模型请求</b>而不是 <c>run.Messages</c>：会话历史由 ChatHistoryProvider
/// 按输入消息记录，装饰器在它下游，因此指令不会随每轮对话在历史里累积。
/// </para>
/// </remarks>
internal sealed class ContextInjectingChatClient(IChatClient inner, string instructions) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => inner.GetResponseAsync(WithContext(messages), options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => inner.GetStreamingResponseAsync(WithContext(messages), options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();

    private IEnumerable<ChatMessage> WithContext(IEnumerable<ChatMessage> messages)
    {
        var injected = new List<ChatMessage> { new(ChatRole.System, instructions) };
        injected.AddRange(messages);
        return injected;
    }
}
