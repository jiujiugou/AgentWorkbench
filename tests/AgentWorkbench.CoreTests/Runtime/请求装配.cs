using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.CoreTests.Runtime;

/// <summary>
/// ① 请求 / 上下文装配的 MAF 装法判据（对应 docs/core/请求装配.md 第一、三、五、六节）。
///
/// 定位：这是「换成 MAF 原生装法之后，框架到底给到哪」的**装配判据**。
/// 生产实现已按这里验证的形状落地（Runtime.Context + AgentHarnessService.BuildAgent），
/// 对应 agent-parts.toml 的 `maf-injection-equivalence`。
///
/// 装配形状刻意与 HistoryProviderProbeTests 一致：
/// FunctionInvokingChatClient 包录制客户端 → ChatClientAgent 用 UseProvidedChatClientAsIs = true 原样使用，
/// 历史交给框架的 InMemoryChatHistoryProvider。指令走 ChatOptions.Instructions，
/// 本仓不再自写 IChatClient 装饰器、不挂 AIContextProviders。
///
/// 每条都写明负控：改坏什么会红。能脚本判定的禁止用 AI 判。
/// </summary>
public sealed class 请求装配
{
    private sealed class EchoTool
    {
        public string Echo(string text) => "echo:" + text;
    }

    /// <summary>记录每次模型请求的消息与选项：回答「模型这一轮实际收到了什么」。</summary>
    private sealed class RecordingChatClient(bool withTool) : IChatClient
    {
        public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((messages.ToList(), options));

            if (withTool && Calls.Count == 1)
            {
                var call = new FunctionCallContent(
                    "call_1", "Echo", new Dictionary<string, object?> { ["text"] = "hi" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "已完成")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static (ChatClientAgent Agent, RecordingChatClient Llm) Build(
        string? instructions, bool withTool = false)
    {
        var llm = new RecordingChatClient(withTool);
        IChatClient pipeline = new FunctionInvokingChatClient(llm);

        var chatOptions = new ChatOptions();
        if (instructions is not null)
            chatOptions.Instructions = instructions;
        if (withTool)
            chatOptions.Tools = [AIFunctionFactory.Create(new EchoTool().Echo, "Echo")];

        var agent = new ChatClientAgent(pipeline, new ChatClientAgentOptions
        {
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions()),
            ChatOptions = chatOptions,
        });
        return (agent, llm);
    }

    private static bool HasInstruction(
        (IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) call, string instruction)
        => (call.Options?.Instructions?.Contains(instruction, StringComparison.Ordinal) ?? false)
           || call.Messages.Any(m => m.Role == ChatRole.System
               && (m.Text?.Contains(instruction, StringComparison.Ordinal) ?? false));

    // 文档职责 5 / 红线 5：指令每轮进请求、不进历史。
    // 负控：把指令塞进 run.Messages（而不是随请求选项下发）→ 会话历史里会出现它，本条红。
    [Fact]
    public async Task 指令每轮都在模型请求里_且不写进会话历史()
    {
        const string instruction = "你是客服";
        var (agent, llm) = Build(instruction, withTool: true);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("请调用 Echo", session);

        Assert.Equal(2, llm.Calls.Count);   // 工具调用轮 + 收口轮
        Assert.True(HasInstruction(llm.Calls[0], instruction), "第一轮模型请求没带指令");
        Assert.True(HasInstruction(llm.Calls[1], instruction), "第二轮模型请求没带指令");

        var state = await agent.SerializeSessionAsync(session, null);
        Assert.False(state.GetRawText().Contains(instruction, StringComparison.Ordinal),
            "指令被写进了会话历史，会随每轮对话累积");
    }

    // 文档职责 6 / 红线 5：无指令时不空包一层、不注入 system。
    // 负控：给 ChatOptions.Instructions 塞值 → 请求里出现指令，本条红。
    [Fact]
    public async Task 没有指令时请求里不出现system消息_空包对照()
    {
        var (agent, llm) = Build(null);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("只有本轮", session);

        Assert.Single(llm.Calls);
        Assert.DoesNotContain(llm.Calls[0].Messages, m => m.Role == ChatRole.System);
        Assert.True(string.IsNullOrWhiteSpace(llm.Calls[0].Options?.Instructions));
    }

    // 文档职责 1：本轮输入永远单独成一条 user 消息，不与历史揉成拼接文本。
    // 负控：把历史与本轮输入拼成一个字符串再发 → 「Text == 第二句」落空，本条红。
    [Fact]
    public async Task 本轮输入独立成一条user消息_不与历史拼接()
    {
        var (agent, llm) = Build(null);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("第一句", session);
        await agent.RunAsync("第二句", session);

        Assert.True(llm.Calls.Count >= 2, "第二轮的会话历史没有被带进请求");
        var last = llm.Calls[^1].Messages;
        Assert.Contains(last, m => m.Role == ChatRole.User && m.Text == "第二句");
        Assert.DoesNotContain(last, m => m.Text is not null
            && m.Text.Contains("第一句", StringComparison.Ordinal)
            && m.Text.Contains("第二句", StringComparison.Ordinal));
    }

    // 文档职责 7 / 替换 negative：工具往返必须完整留在历史里（守卫与轨迹不丢）。
    // 负控：拿掉 FunctionInvokingChatClient 包装、或换装法多包一层环导致往返丢失 → 本条红。
    [Fact]
    public async Task 工具往返完整留在会话历史_守卫与轨迹不丢()
    {
        var (agent, llm) = Build(null, withTool: true);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("请调用 Echo", session);

        Assert.Equal(2, llm.Calls.Count);
        var raw = (await agent.SerializeSessionAsync(session, null)).GetRawText();
        Assert.True(raw.Contains("functionCall", StringComparison.Ordinal), "工具调用没进历史");
        Assert.True(raw.Contains("functionResult", StringComparison.Ordinal), "工具结果没进历史");
    }

    // 文档第五节红线 4 的替换条件：UseProvidedChatClientAsIs 不让 MEA 另包函数调用环。
    // 负控：去掉这个开关 → 工具环被包第二层，模型请求次数或工具执行次数改变，本条红。
    [Fact]
    public async Task UseProvidedChatClientAsIs时工具环不另包一层_两轮模型请求()
    {
        var (agent, llm) = Build(null, withTool: true);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("请调用 Echo", session);

        Assert.Equal(2, llm.Calls.Count);
    }

    // 生产的真实装配形状：agent 级 ChatOptions 带 Instructions，run 级 options 只带 Temperature
    // （AgentHarnessService 的 BuildAgent / RunAsync / StreamRunAsync）。
    // 负控：run 级 options 覆盖掉 agent 级之后指令丢失、或两层指令被拼了两遍 → 本条红。
    [Fact]
    public async Task 生产装配形状_run级带Temperature时指令不丢也不重复()
    {
        const string instruction = "你是客服";
        var (agent, llm) = Build(instruction, withTool: true);
        var session = await agent.CreateSessionAsync();

        await agent.RunAsync("请调用 Echo", session,
            new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.7f }));

        // 工具环仍在：run 级 ChatOptions 没有把 agent 级的 Tools 覆盖掉。
        Assert.Equal(2, llm.Calls.Count);
        foreach (var call in llm.Calls)
        {
            Assert.Equal(instruction, call.Options?.Instructions);
            Assert.Equal(0.7f, call.Options?.Temperature);
            // 指令随选项下发，且没有被转成 system 消息塞进历史消息列表。
            Assert.DoesNotContain(call.Messages, m => m.Role == ChatRole.System);
        }

        // 反例对照：两级都设 Instructions → MAF 把它们换行拼接，同一句指令重复进请求。
        // 这是「指令只设一次」这条生产约束的实证来源；若框架改成「run 级覆盖」，
        // 本条会红，说明 AgentHarnessService.BuildAgent 的注释与 F38 门禁需要重判。
        var (duplicated, dupLlm) = Build(instruction);
        var dupSession = await duplicated.CreateSessionAsync();
        await duplicated.RunAsync("只跑一轮", dupSession,
            new ChatClientAgentRunOptions(new ChatOptions { Temperature = 0.7f, Instructions = instruction }));
        Assert.Equal(instruction + "\n" + instruction, dupLlm.Calls[0].Options?.Instructions);
    }
}
