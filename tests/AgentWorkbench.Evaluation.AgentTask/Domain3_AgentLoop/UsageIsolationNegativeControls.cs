using AgentWorkbench.AgentLoop;
using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentWorkbench.Evaluation.AgentTask.Domain3_AgentLoop;

/// <summary>
/// P2 负控（《评测实现指南》§7，用户已拍板「先建负控，src 不动」）：
/// <c>ModelUsageMeter</c> 是进程级单例，<c>AgentRunGuard</c> 用「当前读数 − 构造时基线」凑本轮用量
/// → 并发两个 run 时，A 烧的 token 会被算进 B 的预算。
///
/// 计时语义（读 AgentRunGuard 源码确认）：预算检查挂在 <c>InspectToolCall</c>——模型**发出工具调用
/// 被检查时**才判预算，迭代开始/模型作答时不判。所以负控里 B 的第二轮必须再发一个工具调用，
/// 污染才会被读到；若第二轮直接答文本，检查永远不会触发（首版测试就这样假绿过）。
///
/// 本文件必须保持红。修复方向（改用 LoopAgent.BuildResult 的逐轮 UsageDetails 累加）落地后，
/// 负控转绿即证明修好了——负控的作用是锚定修复，不是永久红灯。
/// </summary>
public sealed class UsageIsolationNegativeControls
{
    private const long TokensPerRequest = 100;

    private static ChatResponse ToolCall(string mode)
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", mode), ("failTimes", 0), ("delayMs", 0)));

    [Fact]
    public async Task SingleRun_UnderBudget_Succeeds_PositiveControl()
    {
        // 正例：单独跑 B，预算 250、三轮共烧 200（两次检查点：100、200 都在预算内）——必须 succeeded。
        // 没有这条，负控的「红」无法归因（不知道是串扰还是预算本来就不够）。
        var harness = HarnessUnderTest.Create(
            call => call <= 2 ? ToolCall("throw") : ScriptedResponses.Text("done"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxTokensPerRun = 250, MaxRepeatedToolCalls = 0 },
            new MeteredModelRuntime { TokensPerRequest = TokensPerRequest });

        var outcome = await harness.RunAsync("B 单独跑，预算充足");

        Assert.Equal(AgentRunStatus.Succeeded, outcome.Result.Status);
        Assert.Null(outcome.Result.Code);
        Assert.Equal("done", outcome.Result.Text);
        Assert.Equal(2, outcome.ToolCalls);
    }

    [Fact]
    public async Task ConcurrentRun_BudgetIsPollutedByOtherRunsUsage_NegativeControl()
    {
        // 负控：A、B 挂同一个共享计量器（模拟进程级单例）。B 先起（守卫基线取于 0），
        // 第一轮工具调用检查通过后停在工具执行里；A 全程烧完；再放行 B——
        // 第二轮工具调用被检查时，读到的用量已含 A 烧掉的 2000。
        var meter = new UsageMeter();

        var a = HarnessUnderTest.Create(
            _ => ToolCall("throw"),
            new AgentLoopOptions { MaxIterations = 20, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 },
            new MeteredModelRuntime(meter) { TokensPerRequest = TokensPerRequest });

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var b = HarnessUnderTest.Create(
            call => call == 1 ? ToolCall("hang") : ToolCall("throw"),
            new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxTokensPerRun = 250, MaxRepeatedToolCalls = 0 },
            new MeteredModelRuntime(meter) { TokensPerRequest = TokensPerRequest });
        b.Tool.HangGate = gate;

        var bRun = Task.Run(() => b.RunAsync("B 先跑，停在工具执行里"));
        await WaitUntilAsync(() => b.Tool.Calls >= 1);

        var aOutcome = await a.RunAsync("A 并发烧 token");
        Assert.Equal(AgentRunStatus.IterationLimit, aOutcome.Result.Status);

        gate.SetResult();   // 放行 B：第二轮工具调用的预算检查此时读到 100(B) + 2000(A)

        var bOutcome = await bRun;

        // 负控断言：B 被 A 的用量挤爆，且第二个工具调用在执行前就被拦下（没有白花）。
        // 若变成 Succeeded，说明串扰已修（或从未存在）——测试随之失去锚定作用。
        Assert.Equal(AgentRunStatus.BudgetExceeded, bOutcome.Result.Status);
        Assert.Equal(AgentRunCode.BudgetExceeded, bOutcome.Result.Code);
        Assert.Equal(1, bOutcome.ToolCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("等待并发条件超时：B 未在 5 秒内进入第一轮工具执行");
            await Task.Delay(5);
        }
    }
}
