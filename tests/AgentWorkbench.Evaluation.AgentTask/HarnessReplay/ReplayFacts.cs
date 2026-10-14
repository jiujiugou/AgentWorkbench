using AgentWorkbench.AgentHarness;
using Xunit;

namespace AgentWorkbench.Evaluation.AgentTask.HarnessReplay;

/// <summary>
/// 一次 run 的「回放事实」：从轨迹里重建出的三件事——工具调用序列、迭代次数、终态（+ 归类码）。
///
/// 为什么只看这三件：它们是「Agent 到底做了什么、以什么状态结束」的最小完整集。
/// 文本答复是效果层的，会随模型漂移；工具序列 / 迭代 / 终态是机制层的，必须逐次确定。
/// 回放把这三件从「历史轨迹」里读出来，而不是重跑一遍去算。
/// </summary>
internal sealed record ReplayFacts(
    IReadOnlyList<string> ToolSequence, int Iterations, string Status, string? ErrorCode)
{
    /// <summary>从 <see cref="HarnessRunResult"/>（原 run 或回放产物）提取事实。</summary>
    public static ReplayFacts From(HarnessRunResult run) => new(
        run.Steps.Where(step => string.Equals(step.Kind, "tool", StringComparison.Ordinal))
            .Select(step => step.Tool ?? string.Empty)
            .ToList(),
        run.Iterations,
        run.Status,
        run.Code);
}

/// <summary>
/// 回放读取器 + 判据。判据全部自建（《评测实现指南》§2）：MAF 不提供「读历史轨迹并断言」的能力。
/// </summary>
internal static class TraceReplay
{
    /// <summary>
    /// 从历史轨迹回放一次 run。轨迹不存在即抛——「回放不到」绝不能被当成「一切都对」（负控要求：丢轨迹 → 红）。
    /// </summary>
    public static async Task<ReplayFacts> ReadAsync(IAgentHarness harness, long runId)
    {
        var replayed = await harness.GetRunAsync(runId)
            ?? throw new InvalidOperationException($"runId={runId} 的轨迹不存在，无法回放");
        return ReplayFacts.From(replayed);
    }

    /// <summary>只读回放文本（用于验证「回放以截断后的轨迹为准」）。轨迹不存在即抛。</summary>
    public static async Task<string> ReadTextAsync(IAgentHarness harness, long runId)
    {
        var replayed = await harness.GetRunAsync(runId)
            ?? throw new InvalidOperationException($"runId={runId} 的轨迹不存在，无法回放");
        return replayed.Text;
    }

    /// <summary>
    /// 断言「实际回放事实」与「预期事实」逐字段一致；任一字段不符即抛。
    /// 这是本阶段的门禁本体：把预期换成坏形状、或把轨迹篡改，它都必须红——否则回放链是空的。
    /// </summary>
    public static void AssertMatches(ReplayFacts expected, ReplayFacts actual)
    {
        Assert.True(
            expected.Status == actual.Status,
            $"终态不符：预期 {expected.Status}，实际 {actual.Status}");
        Assert.True(
            expected.ErrorCode == actual.ErrorCode,
            $"归类码不符：预期 {Show(expected.ErrorCode)}，实际 {Show(actual.ErrorCode)}");
        Assert.True(
            expected.Iterations == actual.Iterations,
            $"迭代次数不符：预期 {expected.Iterations}，实际 {actual.Iterations}");
        Assert.True(
            expected.ToolSequence.SequenceEqual(actual.ToolSequence),
            $"工具序列不符：预期 [{string.Join(", ", expected.ToolSequence)}]，"
            + $"实际 [{string.Join(", ", actual.ToolSequence)}]");
    }

    private static string Show(string? value) => value is null ? "<null>" : value;
}
