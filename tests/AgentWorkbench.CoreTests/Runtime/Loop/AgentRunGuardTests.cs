using AgentWorkbench.Runtime.Loop;
using AgentWorkbench.Shared;
using Moq;
using Xunit;

namespace AgentWorkbench.CoreTests.Runtime.Loop;

/// <summary>
/// 执行控制的判定：跑满 ≠ 答完、重复调用在发出前停、预算按 run 起点计。
/// 对应 F1.1 / F1.3 / F1.4；全部纯逻辑，不装配 Harness、不碰模型。
/// </summary>
public sealed class AgentRunGuardTests
{
    [Fact]
    public void 跑满迭代_必须记_iteration_limit()
    {
        // MaxIterations = 2 → 最后一个允许的工具轮次是 1。
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxIterations = 2 });
        guard.InspectToolCall(new ToolCallObservation(1, "search_knowledge", "{}"));

        var stop = guard.InspectCompletion();

        Assert.NotNull(stop);
        Assert.Equal(AgentRunStatus.IterationLimit, stop.Status);
        Assert.Equal(AgentRunCode.IterationLimit, stop.Code);
        // 负控 [F1.1]：把 InspectCompletion 的 `< MaxIterations - 1` 改成 `<=` → 本条必须红。
    }

    [Fact]
    public void 未跑满就收口_不得判为截断()
    {
        // 对照组：没有它，上面那条即便「永远返回 stop」也照样绿。
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxIterations = 6 });
        guard.InspectToolCall(new ToolCallObservation(0, "search_knowledge", "{}"));

        Assert.Null(guard.InspectCompletion());
        // 负控 [F1.1]：把 InspectCompletion 恒返回 stop → 本条必须红。
    }

    [Fact]
    public void 一次工具都没调用_谈不上一轮跑满()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxIterations = 1 });

        Assert.Null(guard.InspectCompletion());
        // 负控 [F1.1]：删掉 `_lastIteration < 0` 的提前返回 → 本条必须红。
    }

    [Fact]
    public void 连续同参第三次_在发出前就停()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxRepeatedToolCalls = 3 });

        Assert.Null(guard.InspectToolCall(new ToolCallObservation(0, "t", """{"q":1}""")));
        Assert.Null(guard.InspectToolCall(new ToolCallObservation(1, "t", """{"q":1}""")));
        var stop = guard.InspectToolCall(new ToolCallObservation(2, "t", """{"q":1}"""));

        Assert.NotNull(stop);
        Assert.Equal(AgentRunStatus.LoopDetected, stop.Status);
        // 第三次即返回 stop → 环会 Terminate 且不执行，实际执行次数 = 2 = 上限 − 1。
        // 负控 [F1.3]：去掉 DetectLoop 的指纹比较 → 本条必须红。
    }

    [Fact]
    public void 中间夹了别的调用_重复计数重来()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxRepeatedToolCalls = 3 });

        guard.InspectToolCall(new ToolCallObservation(0, "a", "{}"));
        guard.InspectToolCall(new ToolCallObservation(1, "a", "{}"));

        Assert.Null(guard.InspectToolCall(new ToolCallObservation(2, "b", "{}")));
        // 负控 [F1.3]：把指纹改成只记工具名、不记参数 → 本条仍应绿；
        // 改成「不重置 _repeats」→ 本条必须红。
    }

    [Fact]
    public void 重复检测关闭时不判循环()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxRepeatedToolCalls = 0 });

        for (var i = 0; i < 5; i++)
            Assert.Null(guard.InspectToolCall(new ToolCallObservation(i, "t", "{}")));
        // 负控 [F1.3]：去掉 `<= 0` 的关闭判断 → 本条必须红。
    }

    [Fact]
    public void token预算_按本run起点快照计算()
    {
        var usage = new Mock<IAgentUsageSource>();
        usage.SetupSequence(u => u.GetUsage())
            .Returns(new AgentUsage(0, 0, 0))      // 构造时取一次基线
            .Returns(new AgentUsage(1, 60, 60));   // 判定时读数：本 run 已用 120

        var guard = new AgentRunGuard(
            new AgentLoopOptions { MaxTokensPerRun = 100 }, usage.Object);

        var stop = guard.InspectToolCall(new ToolCallObservation(0, "t", "{}"));

        Assert.NotNull(stop);
        Assert.Equal(AgentRunStatus.BudgetExceeded, stop.Status);
        // 负控 [F1.4]：把「读数 − 基线」改成直接读累计值 → 本条必须红。
    }

    [Fact]
    public void token预算为0_不判()
    {
        var usage = new Mock<IAgentUsageSource>();
        usage.SetupSequence(u => u.GetUsage())
            .Returns(new AgentUsage(0, 0, 0))
            .Returns(new AgentUsage(1, 9999, 9999));

        var guard = new AgentRunGuard(new AgentLoopOptions { MaxTokensPerRun = 0 }, usage.Object);

        Assert.Null(guard.InspectToolCall(new ToolCallObservation(0, "t", "{}")));
        // 负控 [F1.4]：去掉 `<= 0` 的关闭判断 → 本条必须红。
    }
}
