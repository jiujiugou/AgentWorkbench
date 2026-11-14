using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
/// ① 请求装配在 MAF 原生装法下<b>剩下的判定内容</b>：本轮输入的消息形态、停用与空指令、能力位。
///
/// 消息形态、指令注入、历史托管这三样由框架提供，本文件不测框架机制；
/// 「指令每轮进请求且不进历史」「工具往返留在历史」「不另包函数环」在
/// <c>Runtime/请求装配.cs</c> 与 <c>Memory/Persistence/HistoryProviderProbeTests.cs</c> 里验。
///
/// 装配器是 internal sealed，统一通过 AddNitroContext 用 DI 取 IRequestContextComposer。
/// 每条写明负控：改坏什么会红。
/// </summary>
public sealed class ContextComposerTests
{
    /// <summary>用真实 DI 造装配器；configure 在 BuildServiceProvider 前覆盖 ContextOptions。</summary>
    private static IRequestContextComposer CreateComposer(Action<ContextOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroContext(new ConfigurationBuilder().Build());
        if (configure is not null)
            services.Configure<ContextOptions>(configure);
        return services.BuildServiceProvider().GetRequiredService<IRequestContextComposer>();
    }

    /// <summary>
    /// 手工构造 AgentHarnessService 只为调用 GetCapabilitiesAsync：该方法只触碰
    /// IChatClientFactory / IRequestContextComposer / IOptions&lt;MemoryOptions&gt; / IToolCatalog，
    /// 其余构造参数按位置传 null!（构造函数只赋值、不校验，GetCapabilitiesAsync 也不会用到）。
    /// </summary>
    private static IAgentHarness CreateHarness(IRequestContextComposer composer)
    {
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.ResolveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((ChatEndpoint?)null);

        var tools = new Mock<IToolCatalog>();
        tools.Setup(t => t.GetToolNames()).Returns((IReadOnlyList<string>)Array.Empty<string>());

        return new AgentHarnessService(
            factory.Object,                          // IChatClientFactory
            null!,                                    // IModelRuntime
            tools.Object,                            // IToolCatalog
            null!,                                    // IToolExecutionPipeline
            composer,                                // IRequestContextComposer
            null!,                                    // ISessionStore
            new SessionRunGate(),                    // ISessionRunGate
            Options.Create(new MemoryOptions()),     // IOptions<MemoryOptions>
            null!,                                    // IMemoryScopeAccessor
            null!,                                    // IAgentLoop
            null!,                                    // IExecutionTraceStore
            null!,                                    // ILoggerFactory
            null!,                                    // IServiceProvider
            null!);                                   // ILogger<AgentHarnessService>
    }

    // [F3.1] 装配产物：Messages 只有本轮输入一条 user（历史由 provider 追），指令单独产出。
    // 负控：把 system 塞进 Messages、或把历史拼进本轮输入 → 「只有一条」「Text==输入」会红。
    [Fact]
    public void F31_装配产物_消息只有本轮输入且不含system()
    {
        var plan = CreateComposer().Compose(new ContextRequest("本轮问题", "你是客服"));

        var message = Assert.Single(plan.Messages);
        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal("本轮问题", message.Text);
        Assert.Equal("你是客服", plan.Instructions);

        // 反例对照：空请求 → 同样只有一条本轮输入，但没有指令可注入。
        var minimal = CreateComposer().Compose(new ContextRequest("只有本轮"));
        Assert.Single(minimal.Messages);
        Assert.Null(minimal.Instructions);
    }

    // [F3.5] 停用态零注入：Enabled=false → 即便带指令也不产出 Instructions。
    // 负控：把 Compose 里的 Enabled 判断删掉 → 本条红（ChatOptions.Instructions 会被塞进请求）。
    [Fact]
    public async Task F35_停用态零注入_且能力位与真实接线一致()
    {
        var disabled = CreateComposer(o => o.Enabled = false);
        var plan = disabled.Compose(new ContextRequest("本轮", Instructions: "系统指令"));
        Assert.Null(plan.Instructions);
        Assert.Single(plan.Messages);   // 停用态仍只发本轮输入

        // 反例对照：启用态确实产出了指令。
        var enabled = CreateComposer(o => o.Enabled = true);
        Assert.NotNull(enabled.Compose(new ContextRequest("本轮", Instructions: "系统指令")).Instructions);

        // 能力位与配置一致：换 MAF 装法后指令走 ChatOptions.Instructions、历史走 ChatHistoryProvider，
        // 两者都真实接进 run——由 Runtime/请求装配.cs 与 HistoryProviderProbeTests 的 H1/H2 证明。
        // 负控：把 GetCapabilitiesAsync 的 Context 位硬编码 → 本条两条断言必有一条红。
        Assert.False((await CreateHarness(disabled).GetCapabilitiesAsync()).Context);
        Assert.True((await CreateHarness(enabled).GetCapabilitiesAsync()).Context);
    }

    // [F3.6] 空指令：null / 空白 → Instructions=null（不往 ChatOptions 里塞空串）。
    // 负控：让 Compose 在空白时返回 "" 或原样空白 → 本条红。
    [Fact]
    public void F36_无指令时Instructions为null()
    {
        var composer = CreateComposer();

        Assert.Null(composer.Compose(new ContextRequest("只有本轮")).Instructions);
        Assert.Null(composer.Compose(new ContextRequest("只有本轮", Instructions: "   ")).Instructions);

        // 反例对照：有内容时非 null，且首尾空白被压掉（不是原样透传）。
        Assert.Equal("你是客服",
            composer.Compose(new ContextRequest("本轮", Instructions: "  你是客服  ")).Instructions);
    }
}
