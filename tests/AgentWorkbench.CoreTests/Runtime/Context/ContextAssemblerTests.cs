using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkbench.TestKit;
using AgentWorkbench.Runtime.Context;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Runtime.Model;
using AgentWorkbench.Runtime.Session;
using AgentWorkbench.Runtime.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.CoreTests.Runtime.Context;

/// <summary>
/// F3.1–F3.6：上下文组装的消息形态、预算、注入位置、关闭态与装饰器行为。
/// ContextAssembler 是 internal sealed，不能直接 new；统一通过 AddNitroContext 用 DI 取 IContextAssembler。
/// </summary>
public sealed class ContextAssemblerTests
{
    /// <summary>用真实 DI 造组装器；configure 在 BuildServiceProvider 前覆盖 ContextOptions。</summary>
    private static IContextAssembler CreateAssembler(Action<ContextOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroContext(new ConfigurationBuilder().Build());
        if (configure is not null)
            services.Configure<ContextOptions>(configure);
        return services.BuildServiceProvider().GetRequiredService<IContextAssembler>();
    }

    /// <summary>
    /// 手工构造 AgentHarnessService 只为调用 GetCapabilitiesAsync：该方法只触碰
    /// IChatClientFactory / IContextAssembler / IOptions&lt;MemoryOptions&gt; / IToolCatalog，
    /// 其余 9 个构造参数按位置传 null!（构造函数只赋值、不校验，GetCapabilitiesAsync 也不会用到）。
    /// </summary>
    private static IAgentHarness CreateHarness(IContextAssembler assembler)
    {
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.ResolveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((ChatEndpoint?)null);

        var tools = new Mock<IToolCatalog>();
        tools.Setup(t => t.GetToolNames()).Returns((IReadOnlyList<string>)Array.Empty<string>());

        return new AgentHarnessService(
            factory.Object,                          // IChatClientFactory
            null!,                                    // IModelRuntime（不参与能力位计算）
            tools.Object,                            // IToolCatalog
            null!,                                    // IToolExecutionPipeline
            assembler,                               // IContextAssembler
            null!,                                    // ISessionStore
            new SessionRunGate(),                    // ISessionRunGate（只调 GetCapabilitiesAsync，不会被抢）
            Options.Create(new MemoryOptions()),     // IOptions<MemoryOptions>
            null!,                                    // IMemoryScopeAccessor
            null!,                                    // IAgentLoop
            null!,                                    // IExecutionTraceStore
            null!,                                    // ILoggerFactory
            null!,                                    // IServiceProvider
            null!);                                   // ILogger<AgentHarnessService>
    }

    // [F3.1] 组装产物三件：Messages（历史+本轮，不含 system）/ Instructions（指令+片段）/ Fragments（实际注入）
    // 负控：把 Assemble 里 system 塞进 Messages（如 messages.Add(new ChatMessage(ChatRole.System, ...))）
    //       → 「Messages 条数=3」「Messages 无 system」「首尾内容」会红。
    [Fact]
    public void F31_Assemble_ProducesThreeParts_AndMessagesCarryNoSystem()
    {
        var assembler = CreateAssembler();
        var request = new ContextRequest(
            Input: "本轮问题",
            History: new[]
            {
                new ContextTurn("user", "第一句"),
                new ContextTurn("assistant", "第一答"),
            },
            Instructions: "你是客服",
            Fragments: new[] { new ContextFragment("标题甲", "片段正文甲", "doc1") });

        var bundle = assembler.Assemble(request);

        // 正例：三字段的具体值
        Assert.Equal(3, bundle.Messages.Count);                                   // 2 历史 + 1 本轮
        Assert.DoesNotContain(bundle.Messages, m => m.Role == ChatRole.System); // 不含 system
        Assert.Equal(ChatRole.User, bundle.Messages[0].Role);
        Assert.Equal("第一句", bundle.Messages[0].Text);                          // 首条=最旧历史
        Assert.Equal(ChatRole.Assistant, bundle.Messages[1].Role);
        Assert.Equal("第一答", bundle.Messages[1].Text);
        Assert.Equal(ChatRole.User, bundle.Messages[2].Role);
        Assert.Equal("本轮问题", bundle.Messages[2].Text);                        // 尾条=本轮输入

        var instructions = bundle.Instructions;
        Assert.NotNull(instructions);
        Assert.Contains("你是客服", instructions);
        Assert.Contains("【参考片段】", instructions);
        Assert.Contains("标题甲", instructions);

        Assert.Single(bundle.Fragments);
        Assert.Equal("片段正文甲", bundle.Fragments[0].Content);                 // 短片段不截断

        // 反例对照：最小请求（无历史/无指令/无片段）→ 仅 1 条本轮输入，Instructions=null，Fragments 空
        var minimal = assembler.Assemble(new ContextRequest("只有本轮"));
        Assert.Single(minimal.Messages);
        Assert.Equal("只有本轮", minimal.Messages[0].Text);
        Assert.Null(minimal.Instructions);
        Assert.Empty(minimal.Fragments);
    }

    // [F3.2] 历史预算：取最近 N 条，本轮输入必须在内
    // 负控：把 TrimHistory 的 Skip(Count-limit) 改成 Take(limit)（保留最旧）
    //       → 「首条=h10」会红；把本轮输入并进历史一起截断 → 「尾条=本轮输入」会红。
    [Fact]
    public void F32_HistoryBudget_KeepsNewest_AndAlwaysIncludesCurrentInput()
    {
        var history = Enumerable.Range(0, 20)
            .Select(i => new ContextTurn("user", $"h{i}"))
            .ToList();

        // 正例：上限 10 → 保留最近 10 条 + 本轮输入
        var trimmed = CreateAssembler(o => o.MaxHistoryMessages = 10)
            .Assemble(new ContextRequest("本轮输入", History: history));
        Assert.Equal(11, trimmed.Messages.Count);              // 10 历史 + 1 本轮
        Assert.Equal("h10", trimmed.Messages[0].Text);         // 最旧的 h0–h9 被丢弃
        Assert.Equal("h19", trimmed.Messages[9].Text);
        Assert.Equal("本轮输入", trimmed.Messages[10].Text);    // 最后一条必须是本轮输入

        // 反例对照：上限 0（不截断）→ 20 条历史全在，首条是 h0
        var untrimmed = CreateAssembler(o => o.MaxHistoryMessages = 0)
            .Assemble(new ContextRequest("本轮输入", History: history));
        Assert.Equal(21, untrimmed.Messages.Count);
        Assert.Equal("h0", untrimmed.Messages[0].Text);
        Assert.Equal("本轮输入", untrimmed.Messages[20].Text);
    }

    // [F3.3] 片段预算：总字符 ≤ 上限，按序截断
    // 负控：去掉 TrimFragments 的裁剪（直接 return fragments）
    //       → 「长度=1000」「片段总字符=1000」会红（变成 5000）。
    [Fact]
    public void F33_FragmentBudget_TrimsInOrder_AndRespectsCharLimit()
    {
        var big = new string('甲', 5000);

        // 正例：预算 1000 → 单片段截到 1000 字
        var trimmed = CreateAssembler(o => o.MaxFragmentChars = 1000)
            .Assemble(new ContextRequest("本轮", Fragments: new[] { new ContextFragment("大片段", big) }));
        Assert.Single(trimmed.Fragments);
        Assert.Equal(1000, trimmed.Fragments[0].Content.Length);
        Assert.Equal(1000, trimmed.Fragments.Sum(f => f.Content.Length));  // 片段总字符数 = 上限
        Assert.Equal(big[..1000], trimmed.Fragments[0].Content);
        var instr = trimmed.Instructions;
        Assert.NotNull(instr);
        Assert.Contains("【参考片段】", instr);

        // 反例对照：预算 0（不截断）→ 5000 字原样
        var untrimmed = CreateAssembler(o => o.MaxFragmentChars = 0)
            .Assemble(new ContextRequest("本轮", Fragments: new[] { new ContextFragment("大片段", big) }));
        Assert.Equal(5000, untrimmed.Fragments.Sum(f => f.Content.Length));

        // 按序截断：600+600+600，预算 1000 → 第一段全留 600、第二段截到 400、第三段整段丢弃
        var ordered = CreateAssembler(o => o.MaxFragmentChars = 1000)
            .Assemble(new ContextRequest("本轮", Fragments: new[]
            {
                new ContextFragment("一", new string('一', 600)),
                new ContextFragment("二", new string('二', 600)),
                new ContextFragment("三", new string('三', 600)),
            }));
        Assert.Equal(2, ordered.Fragments.Count);
        Assert.Equal(600, ordered.Fragments[0].Content.Length);
        Assert.Equal(400, ordered.Fragments[1].Content.Length);
        Assert.Equal(1000, ordered.Fragments.Sum(f => f.Content.Length));
    }

    // [F3.4] 指令注入：system 进请求、不进历史；多轮之后历史里指令 0 次、请求里 1 次
    // 负控：把指令写进 history / 把 system 塞进 Assemble 的 Messages
    //       → 「bundle.Messages 无 system」「每轮请求恰好 1 条 system」会红（变成随轮次累积）。
    [Fact]
    public async Task F34_Instruction_InjectedIntoRequestOnce_AndNeverIntoHistory()
    {
        var assembler = CreateAssembler();
        var bundle = assembler.Assemble(new ContextRequest("第0轮问题", Instructions: "固定系统指令"));

        // 组装产物里没有 system，指令也不在 Messages 中（不进历史）
        Assert.DoesNotContain(bundle.Messages, m => m.Role == ChatRole.System);
        Assert.DoesNotContain(bundle.Messages, m => m.Text.Contains("固定系统指令"));

        var scripted = new ScriptedChatClient().Reply("答0").Reply("答1").Reply("答2");
        var wrapped = assembler.Wrap(scripted, bundle);

        // 同 session 连跑 3 轮：历史逐轮累积，但历史里始终不含指令 / system
        var history = new List<ChatMessage>();
        for (var round = 0; round < 3; round++)
        {
            history.Add(new ChatMessage(ChatRole.User, $"第{round}轮问题"));
            await wrapped.GetResponseAsync(history.ToList());
            history.Add(new ChatMessage(ChatRole.Assistant, $"第{round}轮答复"));
        }

        Assert.Equal(3, scripted.CallCount);
        for (var i = 0; i < 3; i++)
        {
            var req = scripted.Requests[i];
            // 请求里恰好 1 条 system：装饰器每轮只插一条，不随历史累积
            Assert.Equal(1, req.Count(m => m.Role == ChatRole.System));
            Assert.Equal(ChatRole.System, req[0].Role);
            Assert.Equal("固定系统指令", req[0].Text);
            // 去掉首条 system 后是历史+本轮：里面 system 出现 0 次、指令出现 0 次
            Assert.DoesNotContain(req.Skip(1), m => m.Role == ChatRole.System);
            Assert.DoesNotContain(req.Skip(1), m => m.Text.Contains("固定系统指令"));
        }

        // 反例对照：无注入（bundle 无指令）→ Wrap 原样返回，请求里 system 出现 0 次
        var noInjection = assembler.Assemble(new ContextRequest("只有本轮"));
        var plain = new ScriptedChatClient().Reply("ok");
        var plainWrapped = assembler.Wrap(plain, noInjection);
        Assert.True(ReferenceEquals(plain, plainWrapped));   // 与 F3.6 呼应：无注入原样返回
        await plainWrapped.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "只有本轮") });
        Assert.Equal(0, plain.LastRequest.Count(m => m.Role == ChatRole.System));
    }

    // [F3.5] 关闭态：Enabled=false 零注入，且能力位同步
    // 负控：把 GetCapabilitiesAsync 的 Context 位改回「回显 Options.Enabled」或硬编码 true → 能力位断言会红。
    // 预期红：当前实现是假绿，红是正确信号。
    //   现状：GetCapabilitiesAsync 的 Context 只是把配置开关 Enabled 原样回显（默认 true），
    //   而 Harness 的 PrepareRunAsync 组装时只传 Input/Instructions，不传 History/Fragments，
    //   「历史 + 片段」这套上下文组装能力并未接进实际 run 链路（本轮输入直接进模型）。
    //   因此能力位不得因 Enabled=true 就谎报「上下文能力已启用」——正确行为是 false。
    //   当前实现默认返回 true，故本例最后一条断言红；这是需要修生产代码的信号，不要改测试求绿。
    [Fact]
    public async Task F35_DisabledState_ZeroInjection_AndCapabilityBitIsNotFakeGreen()
    {
        var bundle = CreateAssembler().Assemble(new ContextRequest("本轮", Instructions: "系统指令"));
        var inner = new ScriptedChatClient();

        // (1) 关闭态零注入：Enabled=false → Wrap 原样返回，请求里不会有 system
        var disabled = CreateAssembler(o => o.Enabled = false);
        Assert.True(ReferenceEquals(inner, disabled.Wrap(inner, bundle)));

        // 反例对照：Enabled=true 时确实包了一层装饰器（注入发生）
        var enabled = CreateAssembler(o => o.Enabled = true);
        Assert.False(ReferenceEquals(inner, enabled.Wrap(inner, bundle)));

        // (2) 关闭态能力位：必须为 false（F3.5 可观察结果；当前实现恰好满足）
        var capsDisabled = await CreateHarness(disabled).GetCapabilitiesAsync();
        Assert.False(capsDisabled.Context, "Enabled=false 时能力位必须为 false");

        // (3) 正确行为（预期红）：默认 Enabled=true 也不得谎报 true。
        //     能力位应与「链路是否真的接入上下文组装」同步，而不是与配置开关同步。
        //     当前实现返回 _context.Options.Enabled == true，属于假绿 → 断言 false 会红。
        var capsEnabled = await CreateHarness(enabled).GetCapabilitiesAsync();
        Assert.False(capsEnabled.Context,
            "能力位不得随 Enabled=true 谎报上下文能力已启用：Harness 只把本轮输入直接进模型，" +
            "未接入历史/片段组装；回显 Enabled 属假绿，应为 false。");
    }

    // [F3.6] 无注入时原样返回：Wrap 返回同一实例（ReferenceEquals）
    // 负控：把 Wrap 改成无条件 new ContextInjectingChatClient(inner, ...)
    //       → 本例两条 ReferenceEquals(inner, Wrap(...)) 会红。
    [Fact]
    public void F36_NoInjection_ReturnsSameInnerInstance()
    {
        var assembler = CreateAssembler();

        // 空 bundle（无指令、无片段）→ 原样返回
        var emptyBundle = assembler.Assemble(new ContextRequest("只有本轮"));
        Assert.Null(emptyBundle.Instructions);
        var inner1 = new ScriptedChatClient();
        Assert.True(ReferenceEquals(inner1, assembler.Wrap(inner1, emptyBundle)));

        // Enabled=false 时即使 bundle 带指令也原样返回（关闭态零注入，与 F3.5 呼应）
        var withInstructions = assembler.Assemble(new ContextRequest("本轮", Instructions: "系统指令"));
        var disabled = CreateAssembler(o => o.Enabled = false);
        var inner2 = new ScriptedChatClient();
        Assert.True(ReferenceEquals(inner2, disabled.Wrap(inner2, withInstructions)));

        // 反例对照：有注入内容 + Enabled=true → 返回的是新装饰器，不是同一实例
        var inner3 = new ScriptedChatClient();
        Assert.False(ReferenceEquals(inner3, assembler.Wrap(inner3, withInstructions)));
    }
}
