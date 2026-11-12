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
/// <b>边界</b>：本格起于抢会话闸，止于发起模型调用之前。八步与覆盖情况：
/// <list type="number">
/// <item>抢会话闸 —— 有</item>
/// <item>被拒的 run 不留下轨迹 —— 有</item>
/// <item>流式入口被拒给出明确失败事件 —— 有</item>
/// <item>历史排在本轮输入之前 —— 有</item>
/// <item>指令进模型请求而不是会话历史 —— 有</item>
/// <item>没有指令也没有片段时不注入 system —— 有</item>
/// <item><b>装配工具清单</b> —— <b>没有装置</b>：只能验「把 catalog 的名字原样返回」，
/// 那是转发断言；真正该守的白名单过滤在 <c>ToolCatalog</c>（S04 的问题域）</item>
/// <item><b>发起模型调用</b> —— <b>测不了</b>：模型在 <c>IChatClientFactory</c> 后面，
/// 要对整条循环打桩才能观察，本格不承担</item>
/// </list>
/// 第 7、8 步是登记在案的空白，不是遗漏——它们不写测试，也不该被当成已覆盖。
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

    // [S03-04] 负控：把「本轮输入单独成一条」改成拼进上一条 → 本条红。
    [Fact]
    public void S03_04_历史排在本轮输入之前()
    {
        var bundle = Assembler().Assemble(new ContextRequest(
            "这次的话",
            [new ContextTurn("user", "上一句"), new ContextTurn("assistant", "上一答")]));

        Assert.Collection(bundle.Messages,
            m => Assert.Equal("上一句", m.Text),
            m => Assert.Equal("上一答", m.Text),
            m => Assert.Equal("这次的话", m.Text));
        Assert.Equal(ChatRole.User, bundle.Messages[^1].Role);
    }

    // [S03-05] 负控：把合成结果塞进 Messages 而不是 Instructions → 本条红
    // （否则同一句指令会随每轮对话重复累积）。
    [Fact]
    public void S03_05_指令进模型请求而不是会话历史()
    {
        var bundle = Assembler().Assemble(new ContextRequest("这次的话", null, "你是助手"));

        Assert.NotNull(bundle.Instructions);
        Assert.Contains("你是助手", bundle.Instructions);
        Assert.DoesNotContain(bundle.Messages, m => m.Text?.Contains("你是助手") == true);
    }

    // [S03-06] 负控：让 Compose 在两者都空时返回空串而不是 null → 本条红
    // （每轮会带一条空 system）。
    [Fact]
    public void S03_06_没有指令和片段时不注入system()
    {
        Assert.Null(Assembler().Assemble(new ContextRequest("这次的话")).Instructions);
    }

    private static IContextAssembler Assembler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroContext(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider().GetRequiredService<IContextAssembler>();
    }

    /// <summary>只用到抢闸分支，其余依赖按位置传 null!（构造函数只赋值、不校验）。</summary>
    private static IAgentHarness Harness(ISessionRunGate gate)
        => new AgentHarnessService(
            null!, null!, null!, null!, null!, null!, gate,
            null!, null!, null!, null!, null!, null!, null!);
}
