using System.Text.Json;
using AgentWorkbench.ApplicationTests.Support;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// 证据测试：一次 run 结束时，<see cref="ChatHistoryProvider"/> 拿到的到底是哪几条消息。
///
/// 这条决定了落库 provider 的实现方式。两种可能：
/// - 只拿到 user + 最终 assistant → 工具往返丢失，必须开
///   <c>RequirePerServiceCallChatHistoryPersistence</c>（每次服务调用后持久化），
///   但那会让 FunctionInvokingChatClient 改变行为，风险大。
/// - 拿到完整往返（含 functionCall / functionResult）→ 不必开那个开关，工具环行为不变。
///
/// 实测结论：<b>完整往返都在</b>。所以落库 provider 只需要在
/// <c>StoreChatHistoryAsync</c> 里把 <c>RequestMessages + ResponseMessages</c> 追加进表，
/// 不需要碰 <c>RequirePerServiceCallChatHistoryPersistence</c>。
///
/// 负控：把 <c>FunctionInvokingChatClient</c> 从管道里拿掉（直接给 agent 原始 client），
/// 或把 ChatOptions.Tools 清空 → 模型不会真的调工具，消息数从 4 掉到 2，本测试红。
/// </summary>
public sealed class HistoryProviderProbeTests
{
    private sealed class EchoTool
    {
        public string Echo(string text) => "echo:" + text;
    }

    private static (ChatClientAgent Agent, ScriptedChatClient Llm) BuildAgent(
        InMemoryChatHistoryProvider history)
    {
        var llm = new ScriptedChatClient()
            .ToolCall("Echo", new Dictionary<string, object?> { ["text"] = "hi" })
            .Reply("已完成");

        IChatClient pipeline = new FunctionInvokingChatClient(llm);
        var agent = new ChatClientAgent(pipeline, new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = history,
            ChatOptions = new ChatOptions
            {
                Tools = [AIFunctionFactory.Create(new EchoTool().Echo, "Echo")]
            }
        });
        return (agent, llm);
    }

    [Fact]
    public async Task H1_一次带工具的run_历史里包含完整往返()
    {
        var history = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions());
        var (agent, llm) = BuildAgent(history);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("请调用 Echo", session);

        // 模型被请求了两次：先要调工具，拿到结果后再收口。
        Assert.Equal(2, llm.CallCount);

        // 会话序列化后就是 agent_sessions.state_json 的形状，直接数里面的消息。
        var state = await agent.SerializeSessionAsync(session, null);
        var messages = state.GetProperty("stateBag")
            .GetProperty("InMemoryChatHistoryProvider")
            .GetProperty("messages");

        var roles = messages.EnumerateArray()
            .Select(m => m.GetProperty("role").GetString())
            .ToArray();

        Assert.Equal(new[] { "user", "assistant", "tool", "assistant" }, roles);

        // 关键两条：工具调用与工具结果都在。
        var raw = state.GetRawText();
        Assert.Contains("functionCall", raw, StringComparison.Ordinal);
        Assert.Contains("functionResult", raw, StringComparison.Ordinal);
        Assert.Contains("call_1", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// 落库 provider 要把单条 <see cref="ChatMessage"/> 独立序列化/反序列化。
    /// 这条验证 M.E.AI 的默认选项能正确往返 functionCall，不会退化成纯文本。
    /// 负控：换成 <c>new JsonSerializerOptions()</c> → functionCall 内容会丢，断言红。
    /// </summary>
    [Fact]
    public void H2_单条消息可按默认选项往返_functionCall不丢()
    {
        var original = new ChatMessage(
            ChatRole.Assistant,
            [new FunctionCallContent("call_9", "Echo", new Dictionary<string, object?> { ["text"] = "hi" })]);

        var json = JsonSerializer.Serialize(original, AIJsonUtilities.DefaultOptions);
        var round = JsonSerializer.Deserialize<ChatMessage>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(round);
        Assert.Equal(ChatRole.Assistant, round!.Role);
        var call = Assert.Single(round.Contents.OfType<FunctionCallContent>());
        Assert.Equal("call_9", call.CallId);
        Assert.Equal("Echo", call.Name);
    }
}
