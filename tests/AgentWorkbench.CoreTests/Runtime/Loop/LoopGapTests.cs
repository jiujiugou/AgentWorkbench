using System.Reflection;
using AgentWorkbench.Runtime.Loop;
using AgentWorkbench.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.CoreTests.Runtime.Loop;

/// <summary>
/// 七项职责的缺口补锁（现有 13 条见同目录 AgentLoopServiceTests / AgentRunGuardTests）：
/// ⑥ 终态与归类词表的完整性（配对 + 落库长度约束）、⑦ 停下来不能记成成功的正面锁定、
/// ② 职责里「超时必须是真取消」的穿透验证（令牌链进动作，不是外层等时间后放弃）。
///
/// 职责 5「判定时机：环内、执行前」是跨模块行为，判定点在 Runtime.Tools 的 FunctionInvoker，
/// 其测试归工具执行方向（S04 流程验收 / ToolCallGapTests），不在此重复。
/// 每条负控写在断言旁的注释里：改坏哪里必须红。
/// </summary>
public sealed class LoopGapTests
{
    private static AgentLoopService Loop(int timeoutSeconds = 30, int maxIterations = 6)
        => new(Options.Create(new AgentLoopOptions
        {
            TimeoutSeconds = timeoutSeconds,
            MaxIterations = maxIterations
        }), NullLogger<AgentLoopService>.Instance);

    // ── 职责 6 + 7：终止判定的词表纪律 ──────────────────────────────

    [Fact]
    public void 三种终止判定的状态与归类码一一配对且都不是成功()
    {
        // iteration_limit：顶到最后一轮（AgentRunGuardTests 只断言了 Status，这里加配对与「非成功」）
        var guard1 = new AgentRunGuard(new AgentLoopOptions { MaxIterations = 2 });
        guard1.InspectToolCall(new ToolCallObservation(1, "t", "{}"));
        var s1 = guard1.InspectCompletion();

        // loop_detected：连续同参到阈值
        var guard2 = new AgentRunGuard(new AgentLoopOptions { MaxRepeatedToolCalls = 2 });
        guard2.InspectToolCall(new ToolCallObservation(0, "t", "{}"));
        var s2 = guard2.InspectToolCall(new ToolCallObservation(1, "t", "{}"));

        // budget_exceeded：读数 − 基线 超限
        var source = new FakeUsage(new AgentUsage(0, 0, 0), new AgentUsage(1, 500, 500));
        var guard3 = new AgentRunGuard(new AgentLoopOptions { MaxTokensPerRun = 100 }, source);
        var s3 = guard3.InspectToolCall(new ToolCallObservation(0, "t", "{}"));

        Assert.Multiple(
            () => Assert.Equal((AgentRunStatus.IterationLimit, AgentRunCode.IterationLimit), (s1!.Status, s1.Code)),
            () => Assert.Equal((AgentRunStatus.LoopDetected, AgentRunCode.LoopDetected), (s2!.Status, s2.Code)),
            () => Assert.Equal((AgentRunStatus.BudgetExceeded, AgentRunCode.BudgetExceeded), (s3!.Status, s3.Code)),
            // 职责 7 的正面锁：任何终止判定都不许把「停」记成「成功」
            () => Assert.NotEqual(AgentRunStatus.Succeeded, s1!.Status),
            () => Assert.NotEqual(AgentRunStatus.Succeeded, s2!.Status),
            () => Assert.NotEqual(AgentRunStatus.Succeeded, s3!.Status)
        );
        // 负控：把任一判定路径的 Code 换成别的词表常量 / 把 Status 写成 Succeeded → 本条必须红。
    }

    [Fact]
    public void 终态状态码满足落库约束_不超过16字符()
    {
        // agent_runs.status 列宽 ≤16（迁移期定死）；词表常量超长会在落库时被截断或报错——锁在源头。
        var statuses = typeof(AgentRunStatus).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string) && f.IsInitOnly == false && f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.True(statuses.Count >= 7, $"词表应至少含 README 列出的 7 个状态，实际 {statuses.Count} 个");
        Assert.All(statuses, s => Assert.True(
            s.Length <= 16, $"状态码「{s}」长 {s.Length}，超过 agent_runs.status 的 16 字符列宽"));
        Assert.Equal(statuses.Count, statuses.Distinct().Count());
        // 负控：把任一状态常量写成超过 16 字符的串 → 本条必须红。
    }

    // ── 职责 2：超时必须是真取消 ────────────────────────────────────

    [Fact]
    public async Task 超时是真取消_动作内部能观察到令牌被取消()
    {
        var loop = Loop(timeoutSeconds: 1);
        var observed = false;

        await Assert.ThrowsAsync<AgentRunTimeoutException>(() =>
            loop.ExecuteAsync<string>(async ct =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    observed = ct.IsCancellationRequested;
                    throw;
                }
                return "未超时";
            }));

        Assert.True(observed,
            "动作内部没有观察到令牌取消——实现退化成了「外层等时间后放弃」，没有把取消链进正在跑的动作");
        // 负控：把 ExecuteAsync 的实现改成外层 Task.WhenAny 等待后放弃（不链 linked CTS）
        // → 动作收不到取消、observed 保持 false → 本条必须红。
    }

    [Fact]
    public async Task 调用方取消也穿透_动作同样能观察到()
    {
        // 对照组：证明上面那条不是「ExecuteAsync 的 ct 永远不取消」的假绿。
        var loop = Loop(timeoutSeconds: 30);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var observed = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            loop.ExecuteAsync<string>(async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    observed = ct.IsCancellationRequested;
                    throw;
                }
                return "未取消";
            }, cts.Token));

        Assert.True(observed);
        // 负控：ExecuteAsync 若丢掉调用方令牌（不 Link）→ 动作永远挂着、测试超时 → 本条必须红。
    }

    /// <summary>按 SetupSequence 顺序回放用量读数的假用量源（替代 Moq，保持零依赖噪音）。</summary>
    private sealed class FakeUsage(params AgentUsage[] readings) : IAgentUsageSource
    {
        private int _i;
        public AgentUsage GetUsage() => readings[Math.Min(_i++, readings.Length - 1)];
    }
}
