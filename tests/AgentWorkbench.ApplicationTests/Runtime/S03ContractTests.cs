using AgentWorkbench.Runtime.Context;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Runtime.Session;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// S03（Harness 组上下文）的验收装置。<b>一个方法 = 一个步骤功能</b>：方法名说的是「这一步做什么」，
/// 绿还是红由运行给出——名字不当断言用，断言在方法体里。
///
/// <b>三处各说一件事，互不重复</b>：方法名 = 步骤功能；注释 = 改哪里会红 + 边界；
/// <c>docs/运行期流程.md</c> = 为什么要有、不许留下什么、归谁。
///
/// <b>边界</b>：本格起于抢会话闸，止于发起模型调用之前。文档 S03「交给下一格什么」里还有两样
/// 不在这里验：<b>工具白名单 / 能力清单</b>归 S04（过滤在 <c>ToolCatalog</c>），
/// <b>发起模型调用</b>及「模型不可用时轨迹照落」需要对整条循环打桩。
/// 这两处是登记在案的空白，不是遗漏，也不写占位测试——S1 那条 Skip 的用法不在这里复用。
///
/// <b>层 × 方向</b>：01–03 是 Harness 的拒止路径（Core，闸门）；04–10 是请求装配（Core，请求）。
/// 全格没有一步属 Host，也没有一步属域。
///
/// <b>纪律</b>：行为断言，不是源码字符串断言；每条都写明了改哪里会红。恒真的绿等于没验。
/// </summary>
public sealed class S03ContractTests
{
    // [S03-01] 负控：删掉 RunAsync 里的抢闸分支 → 本条红（会继续走到 _memoryScope 抛空引用）。
    [Fact]
    public async Task S03_01_同一会话只允许一个run()
    {
        var gate = new SessionRunGate();
        await using var held = await gate.TryAcquireAsync("s03-01");

        var result = await Harness(gate).RunAsync(new HarnessRunRequest("s03-01", "第二句"));

        Assert.Equal(AgentRunCode.SessionBusy, result.Code);
        Assert.Equal(AgentRunStatus.Failed, result.Status);
    }

    // [S03-02] 负控：抢不到闸时改成「先落一条轨迹再返回」→ 本条红。
    // 这与 S03-01 是两个契约：前者说「拒绝」，这条说「拒绝不产生一次 run」。
    [Fact]
    public async Task S03_02_被拒的run不留下轨迹()
    {
        var gate = new SessionRunGate();
        await using var held = await gate.TryAcquireAsync("s03-02");

        var result = await Harness(gate).RunAsync(new HarnessRunRequest("s03-02", "第二句"));

        Assert.Equal(0, result.RunId);
        Assert.Empty(result.Steps);
    }

    // [S03-03] 负控：抢不到闸时改成 yield break（不发事件）→ 本条红（前端会静默挂着）。
    [Fact]
    public async Task S03_03_流式入口被拒时给出明确失败事件()
    {
        var gate = new SessionRunGate();
        await using var held = await gate.TryAcquireAsync("s03-03");

        var events = new List<HarnessStreamEvent>();
        await foreach (var evt in Harness(gate).StreamAsync(new HarnessRunRequest("s03-03", "第二句")))
            events.Add(evt);

        var single = Assert.Single(events);
        Assert.Equal(HarnessStreamEventKind.Failed, single.Kind);
        Assert.Equal(AgentRunCode.SessionBusy, single.Code);
        Assert.False(string.IsNullOrWhiteSpace(single.Error));
    }

    // [S03-04] 负控：把「本轮输入单独成一条」改成拼进上一条、或塞进 Instructions → 本条红。
    // 「历史排在本轮之前」不再由本模块保证：历史交给 MAF 的 ChatHistoryProvider 托管
    // （2026-11-14 重构，`ContextRequest` 删掉了 History 参数），装配只产出本轮输入一条。
    [Fact]
    public void S03_04_本轮输入单独成一条user消息()
    {
        var plan = Composer().Compose(new ContextRequest("这次的话"));

        var only = Assert.Single(plan.Messages);
        Assert.Equal(ChatRole.User, only.Role);
        Assert.Equal("这次的话", only.Text);
    }

    // [S03-05] 负控：把合成结果塞进 Messages 而不是 Instructions → 本条红
    // （否则同一句指令会随每轮对话重复累积）。
    [Fact]
    public void S03_05_指令进模型请求而不是会话历史()
    {
        var plan = Composer().Compose(new ContextRequest("这次的话", "你是助手"));

        Assert.NotNull(plan.Instructions);
        Assert.Contains("你是助手", plan.Instructions);
        Assert.DoesNotContain(plan.Messages, m => m.Text?.Contains("你是助手") == true);
    }

    // [S03-06] 负控：让 Compose 在无指令时返回空串而不是 null → 本条红
    // （每轮会带一条空 system）。
    [Fact]
    public void S03_06_没有指令时不注入system()
    {
        Assert.Null(Composer().Compose(new ContextRequest("这次的话")).Instructions);
    }

    // [S03-07] 负控：把会话闸改成全局一把锁（不看 sessionId）→ 本条红。
    // 文档 S03「不许留下什么」明写：互斥键是 sessionId，不是连接。
    [Fact]
    public async Task S03_07_不同会话互不干扰()
    {
        var gate = new SessionRunGate();
        await using var first = await gate.TryAcquireAsync("s03-07-a");
        await using var second = await gate.TryAcquireAsync("s03-07-b");

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    // [S03-08] 负控：删掉 Compose 里对本轮输入的判空 → 本条红
    // （空输入会变成一条空 user 消息，模型在半截上下文里继续推理）。
    [Fact]
    public void S03_08_空本轮输入被拒绝()
    {
        Assert.Throws<ArgumentException>(() => Composer().Compose(new ContextRequest("   ")));
    }

    // [S03-09] 负控：把 Compose 的 IsNullOrWhiteSpace 换成 IsNullOrEmpty → 本条红
    // （空白串会被塞进 ChatOptions.Instructions，每轮带一条空白 system）。
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void S03_09_空白指令等同没有指令(string instructions)
    {
        Assert.Null(Composer().Compose(new ContextRequest("这次的话", instructions)).Instructions);
    }

    // [S03-10] 负控：让停用态仍然注入指令 → 本条红。
    // 开关是 ContextOptions.Enabled（配置节 Harness:Context）；关闭后 Instructions 恒为 null。
    [Fact]
    public void S03_10_停用装配时不注入指令()
    {
        Assert.Null(Composer(enabled: false)
            .Compose(new ContextRequest("这次的话", "你是助手")).Instructions);
    }

    private static IRequestContextComposer Composer(bool enabled = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Harness:Context:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroContext(configuration);
        return services.BuildServiceProvider().GetRequiredService<IRequestContextComposer>();
    }

    /// <summary>只用到抢闸分支，其余依赖按位置传 null!（构造函数只赋值、不校验）。</summary>
    private static IAgentHarness Harness(ISessionRunGate gate)
        => new AgentHarnessService(
            null!, null!, null!, null!, null!, null!, gate,
            null!, null!, null!, null!, null!, null!, null!);
}
