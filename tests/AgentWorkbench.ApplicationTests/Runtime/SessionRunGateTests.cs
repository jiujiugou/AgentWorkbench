using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Runtime.Session;
using AgentWorkbench.Shared;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// 会话互斥（L1，进程内）。
///
/// 不变量：同一 <c>sessionId</c> 同一时刻最多一个 run 在推进；第二个请求必须被<b>拒绝</b>
/// （不是并发执行，也不是静默丢弃），并返回 <c>session_busy</c>。
///
/// 追加式历史解决了「丢」，没解决「乱」——两个 run 仍会把对话交错写进同一会话，
/// 模型在半截上下文里继续推理。这一层挡的是「乱」。
///
/// 覆盖边界：锁在内存里，只有单进程成立。跨进程要数据库租约（未实现）。
/// </summary>
public sealed class SessionRunGateTests
{
    // [G1] 同一会话第二次抢占必须拿不到；释放之后必须能再抢到。
    // 负控：TryAcquireAsync 改成恒返回句柄 → second 不为 null，Assert 红。
    [Fact]
    public async Task G1_同一会话只能有一个持有者_释放后可重抢()
    {
        var gate = new SessionRunGate();

        var first = await gate.TryAcquireAsync("s1");
        Assert.NotNull(first);

        var second = await gate.TryAcquireAsync("s1");
        Assert.Null(second);

        await first!.DisposeAsync();

        var third = await gate.TryAcquireAsync("s1");
        Assert.NotNull(third);
        await third!.DisposeAsync();
    }

    // [G2] 不同会话互不影响——会话隔离是这套系统的基本前提，不能因为加了闸就退化成全局串行。
    // 负控：把字典键改成常量（所有会话共用一个键）→ second 为 null，Assert 红。
    [Fact]
    public async Task G2_不同会话可以同时持有()
    {
        var gate = new SessionRunGate();

        var a = await gate.TryAcquireAsync("session-a");
        var b = await gate.TryAcquireAsync("session-b");

        Assert.NotNull(a);
        Assert.NotNull(b);

        await a!.DisposeAsync();
        await b!.DisposeAsync();
    }

    // [G3] 重复释放不得删掉别人的占位。
    // 主保护是句柄自己的 _released 标志：第二次 Dispose 直接返回，不碰字典。
    // 负控（已验证）：去掉 _released、每次 Dispose 都 TryRemove(key) → 本用例红。
    // 注：TryRemove(KeyValuePair) 是防御性冗余，不是必需——这一点是负控没变红时发现的，
    //     最初把「必须用 KeyValuePair」写进注释，是错的。
    [Fact]
    public async Task G3_重复释放不影响后来的持有者()
    {
        var gate = new SessionRunGate();

        var first = await gate.TryAcquireAsync("s");
        await first!.DisposeAsync();               // 正常释放

        var second = await gate.TryAcquireAsync("s");
        Assert.NotNull(second);

        await first.DisposeAsync();                // 已释放的句柄再释放一次（迟到 / 重复调用）

        var third = await gate.TryAcquireAsync("s");
        Assert.Null(third);                        // 仍然被 second 占着

        await second!.DisposeAsync();
    }

    // [G4] Harness 在会话被占用时必须回 session_busy，而不是开始跑。
    // 构造时其余依赖传 null!：抢不到闸的分支在触碰任何依赖之前就返回（RunId=0 也是这个含义）。
    // 负控：删掉 RunAsync 里的抢闸分支 → 本次会继续走到 _memoryScope 并抛 NullReferenceException，Assert 红。
    [Fact]
    public async Task G4_会话被占用时_run返回session_busy而不执行()
    {
        var gate = new SessionRunGate();
        await using var held = await gate.TryAcquireAsync("busy-session");

        var harness = new AgentHarnessService(
            null!, null!, null!, null!, null!, null!, gate,
            null!, null!, null!, null!, null!, null!, null!);

        var result = await harness.RunAsync(new HarnessRunRequest("busy-session", "随便说点什么"));

        Assert.Equal(AgentRunCode.SessionBusy, result.Code);
        Assert.Equal(AgentRunStatus.Failed, result.Status);
        Assert.Equal(0, result.RunId);            // 没有开始跑，就没有轨迹
        Assert.Empty(result.Steps);
    }

    // [G5] 流式入口同一语义：只产出一个 Failed 事件，Code=session_busy。
    // 注意它必须是「事件」而不是异常——抛异常会被 Hub 当成服务故障，与「会话忙」混在一起。
    [Fact]
    public async Task G5_会话被占用时_流式入口只产出一个session_busy事件()
    {
        var gate = new SessionRunGate();
        await using var held = await gate.TryAcquireAsync("busy-stream");

        var harness = new AgentHarnessService(
            null!, null!, null!, null!, null!, null!, gate,
            null!, null!, null!, null!, null!, null!, null!);

        var events = new List<HarnessStreamEvent>();
        await foreach (var evt in harness.StreamAsync(new HarnessRunRequest("busy-stream", "随便说点什么")))
            events.Add(evt);

        var single = Assert.Single(events);
        Assert.Equal(HarnessStreamEventKind.Failed, single.Kind);
        Assert.Equal(AgentRunCode.SessionBusy, single.Code);
        Assert.False(string.IsNullOrWhiteSpace(single.Error));
    }
}
