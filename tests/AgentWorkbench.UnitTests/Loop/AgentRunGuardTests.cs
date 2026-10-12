using AgentWorkbench.AgentLoop;
using AgentWorkbench.Shared;
using Xunit;

namespace AgentWorkbench.UnitTests.Loop;

/// <summary>
/// 判定型：执行控制守卫必须把「卡在同一个调用上」与「把 token 烧光」在环内判出来，
/// 并把「跑满迭代被强制收口」与「模型答完了」分开。
///
/// 每个正例都配同形状的对照（只动旋钮）：0 = 关闭该项检测，用来证明正例不是恒真。
/// </summary>
public sealed class AgentRunGuardTests
{
    [Fact]
    public void InspectToolCall_RepeatedIdenticalCalls_StopsAtConfiguredThreshold()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxRepeatedToolCalls = 3 });

        Assert.Null(guard.InspectToolCall(Call(iteration: 0)));
        Assert.Null(guard.InspectToolCall(Call(iteration: 1)));
        var stop = guard.InspectToolCall(Call(iteration: 2));

        Assert.NotNull(stop);
        Assert.Equal(AgentRunStatus.LoopDetected, stop.Status);
        Assert.Equal(AgentRunCode.LoopDetected, stop.Code);
    }

    [Fact]
    public void InspectToolCall_RepeatedCalls_WithDetectionDisabled_NeverStops()
    {
        // 负控：同一串调用，只把阈值拧到 0 —— 检测关闭，永远不该停。
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxRepeatedToolCalls = 0 });

        for (var i = 0; i < 20; i++)
            Assert.Null(guard.InspectToolCall(Call(i)));
    }

    [Fact]
    public void InspectToolCall_NonConsecutiveRepeats_DoesNotStop()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxRepeatedToolCalls = 3 });

        // A A B A A：同样的调用出现了 4 次，但从不「连续」3 次 —— 夹了别的调用就重算，不能误伤。
        Assert.Null(guard.InspectToolCall(Call(0, query: "a")));
        Assert.Null(guard.InspectToolCall(Call(1, query: "a")));
        Assert.Null(guard.InspectToolCall(Call(2, query: "b")));
        Assert.Null(guard.InspectToolCall(Call(3, query: "a")));
        Assert.Null(guard.InspectToolCall(Call(4, query: "a")));
    }

    [Fact]
    public void InspectToolCall_SameToolDifferentArguments_IsNotARepeat()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxRepeatedToolCalls = 2 });

        // 指纹含参数：同名工具换了参数是在推进任务，不是循环。
        Assert.Null(guard.InspectToolCall(Call(0, query: "a")));
        Assert.Null(guard.InspectToolCall(Call(1, query: "b")));
        Assert.Null(guard.InspectToolCall(Call(2, query: "c")));
    }

    [Fact]
    public void InspectToolCall_TokenUsageOverBudget_Stops()
    {
        var usage = new FakeUsageSource(new AgentUsage(0, 0, 0));
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxTokensPerRun = 100 }, usage);

        usage.Current = new AgentUsage(1, 100, 0);   // 刚好用满，不算超
        Assert.Null(guard.InspectToolCall(Call(0)));

        usage.Current = new AgentUsage(2, 101, 0);   // 超一分钱也要停
        var stop = guard.InspectToolCall(Call(1));

        Assert.NotNull(stop);
        Assert.Equal(AgentRunStatus.BudgetExceeded, stop.Status);
        Assert.Equal(AgentRunCode.BudgetExceeded, stop.Code);
    }

    [Fact]
    public void InspectToolCall_TokenBudget_CountsOnlyThisRunsDelta()
    {
        // 基线不为 0：读数里含着别的 run 的历史消耗，判定必须只看「本 run 起点之后」的增量。
        var usage = new FakeUsageSource(new AgentUsage(5, 10_000, 0));
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxTokensPerRun = 100 }, usage);

        usage.Current = new AgentUsage(6, 10_050, 0);
        Assert.Null(guard.InspectToolCall(Call(0)));

        usage.Current = new AgentUsage(7, 10_200, 0);
        Assert.NotNull(guard.InspectToolCall(Call(1)));
    }

    [Fact]
    public void InspectToolCall_TokenUsage_WithBudgetDisabledOrNoSource_NeverStops()
    {
        // 负控一：预算 0 = 不限额。
        var usage = new FakeUsageSource(new AgentUsage(0, 0, 0));
        var unlimited = new AgentRunGuard(new AgentLoopOptions { MaxTokensPerRun = 0 }, usage);
        usage.Current = new AgentUsage(9, 1_000_000, 0);
        Assert.Null(unlimited.InspectToolCall(Call(0)));

        // 负控二：没有用量来源（未装配）时不判预算。
        var noSource = new AgentRunGuard(new AgentLoopOptions { MaxTokensPerRun = 1 });
        Assert.Null(noSource.InspectToolCall(Call(0)));
    }

    [Fact]
    public void InspectCompletion_LastAllowedIteration_ReportsIterationLimit()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxIterations = 6 });

        Assert.Null(guard.InspectToolCall(Call(iteration: 5)));
        var stop = guard.InspectCompletion();

        Assert.NotNull(stop);
        Assert.Equal(AgentRunStatus.IterationLimit, stop.Status);
        Assert.Equal(AgentRunCode.IterationLimit, stop.Code);
    }

    [Fact]
    public void InspectCompletion_ModelStoppedCallingToolsEarly_IsNotIterationLimit()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxIterations = 6 });

        Assert.Null(guard.InspectToolCall(Call(iteration: 0)));
        Assert.Null(guard.InspectCompletion());
    }

    [Fact]
    public void InspectCompletion_NoToolCallsAtAll_IsNotIterationLimit()
    {
        var guard = new AgentRunGuard(new AgentLoopOptions { MaxIterations = 6 });

        // 模型直接答完，一次工具都没调：谈不上「跑满」。
        Assert.Null(guard.InspectCompletion());
    }

    private static ToolCallObservation Call(int iteration, string query = "q")
        => new(iteration, "search", $"{{\"query\":\"{query}\"}}");

    private sealed class FakeUsageSource(AgentUsage initial) : IAgentUsageSource
    {
        public AgentUsage Current { get; set; } = initial;

        public AgentUsage GetUsage() => Current;
    }
}
