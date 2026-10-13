using Microsoft.Extensions.AI;

namespace AgentWorkbench.AgentHarness;

/// <summary>
/// 按 run 注入系统指令的模型客户端装饰器：转发前把一条 <see cref="ChatRole.System"/> 消息插到最前。
/// </summary>
/// <remarks>
/// <b>为什么不用 MEA 的 <c>AIContextProviders</c></b>：只在 Agent 上挂 provider 会让 MEA 在
/// 「我们自己装配的 <c>FunctionInvokingChatClient</c>」<b>外面</b>再包一层函数调用环，
/// 于是 <c>ToolExecutionPipeline</c> 的守卫与轨迹采集全部被绕过——实测症状是连续同参调用
/// 不再判 <c>loop_detected</c>、工具异常不再归类 <c>tool_error</c>（本是 <c>succeeded</c>）。
/// 装饰器插在这一层，调用链一个节点都不动。
///
/// 指令进的是<b>模型请求</b>而不是 <c>run.Messages</c>：会话历史由 ChatHistoryProvider 按输入消息记录，
/// 装饰器在它下游，所以指令不会随每轮对话在历史里累积。
/// </remarks>
internal sealed class RunInstructionsChatClient(IChatClient inner, string instructions) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => inner.GetResponseAsync(WithInstructions(messages), options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => inner.GetStreamingResponseAsync(WithInstructions(messages), options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();

    private IEnumerable<ChatMessage> WithInstructions(IEnumerable<ChatMessage> messages)
    {
        var injected = new List<ChatMessage> { new(ChatRole.System, instructions) };
        injected.AddRange(messages);
        return injected;
    }
}
