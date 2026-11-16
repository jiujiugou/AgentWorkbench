namespace AgentWorkbench.Evaluation.Kernel;

/// <summary>
/// N 次采样的聚合：回答「多少 / 多贵 / 多慢 / 稳不稳」这四类问题。
/// </summary>
/// <remarks>
/// <b>它不是分数。</b> 这里只有分布与均值，没有 pass@1、没有加权总分、没有通过线。
/// 那条线在 v4 里是冻结的（「功能数量、覆盖率、绿灯数一律不是成功标准」），
/// 而「十次里几次触发守卫」是分布，不是成绩。
///
/// <b>它不跑任何东西。</b> 采样由调用方做（假模型验聚合算法，真模型验分布），
/// 本类型只做纯计算。所以它可以在单元测试里用手写的样本喂满，不需要模型、不需要存储。
///
/// <b>缺失轨迹单独计数，不进均值。</b> 见 <see cref="KernelSample"/> 的说明——
/// 把「读不回来」的 0 混进均值，会让存储故障显示成「又快又省」。
/// </remarks>
public sealed record KernelReport(
    int Runs,
    int Passed,
    int MissingTrace,
    double PassRate,
    double AvgIterations,
    int MinIterations,
    int MaxIterations,
    double AvgToolSteps,
    double AvgLatencyMs,
    long P95LatencyMs,
    long? AvgInputTokens,
    long? AvgOutputTokens,
    IReadOnlyDictionary<string, int> StopKinds,
    IReadOnlyDictionary<string, int> Statuses)
{
    /// <summary>聚合一组采样。</summary>
    /// <exception cref="ArgumentException">采样为空——空报告会被读成「零失败」，那是假绿。</exception>
    public static KernelReport From(IReadOnlyList<KernelSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
            throw new ArgumentException(
                "采样为空：空报告会被读成「零失败」，不要用它冒充通过。", nameof(samples));

        var runs = samples.Count;
        var passed = samples.Count(s => s.AllPassed);

        // 数字统计只看读到轨迹的那些：缺失样本的 0 是「不知道」，不是「很小」。
        var withTrace = samples.Where(s => s.HasTrace).ToArray();
        var iterations = withTrace.Select(s => s.Iterations).ToArray();
        var latencies = withTrace.Select(s => s.LatencyMs).OrderBy(x => x).ToArray();
        var inputs = withTrace.Where(s => s.InputTokens.HasValue).Select(s => s.InputTokens!.Value).ToArray();
        var outputs = withTrace.Where(s => s.OutputTokens.HasValue).Select(s => s.OutputTokens!.Value).ToArray();

        return new KernelReport(
            Runs: runs,
            Passed: passed,
            MissingTrace: runs - withTrace.Length,
            PassRate: (double)passed / runs,
            AvgIterations: Mean(iterations),
            MinIterations: iterations.Length == 0 ? 0 : iterations.Min(),
            MaxIterations: iterations.Length == 0 ? 0 : iterations.Max(),
            AvgToolSteps: withTrace.Length == 0 ? 0 : withTrace.Average(s => (double)s.ToolSteps),
            AvgLatencyMs: Mean(latencies),
            P95LatencyMs: Percentile(latencies, 0.95),
            // 全部没上报时是 null（不是 0）——「供应商没给」与「用了零 token」必须分得开。
            AvgInputTokens: inputs.Length == 0 ? null : (long)inputs.Average(),
            AvgOutputTokens: outputs.Length == 0 ? null : (long)outputs.Average(),
            StopKinds: Tally(withTrace.Select(s => s.StopKind)),
            Statuses: Tally(samples.Select(s => s.Status)));
    }

    /// <summary>一行给人看的摘要。写进日志或贴在提交说明里，比一串字段好读。</summary>
    public override string ToString()
        => $"runs={Runs} passed={Passed} missing={MissingTrace} passRate={PassRate:F2} "
           + $"iters={AvgIterations:F1}[{MinIterations},{MaxIterations}] toolSteps={AvgToolSteps:F1} "
           + $"latency={AvgLatencyMs:F0}ms p95={P95LatencyMs}ms "
           + $"stop=[{Format(StopKinds)}] status=[{Format(Statuses)}]";

    private static double Mean(int[] values) => values.Length == 0 ? 0 : values.Average();

    private static double Mean(long[] values) => values.Length == 0 ? 0 : values.Average();

    /// <summary>最近秩法：<c>ceil(p·n) − 1</c>，越界夹到两端。样本少时 P95 就是最大值，这是有意为之。</summary>
    private static long Percentile(long[] sortedAscending, double p)
    {
        if (sortedAscending.Length == 0)
            return 0;

        var rank = (int)Math.Ceiling(p * sortedAscending.Length) - 1;
        return sortedAscending[Math.Clamp(rank, 0, sortedAscending.Length - 1)];
    }

    /// <summary>计数，忽略 null / 空串（「没有停因」不该被记成一个叫 null 的桶）。</summary>
    private static IReadOnlyDictionary<string, int> Tally(IEnumerable<string?> keys)
        => keys.Where(k => !string.IsNullOrEmpty(k))
            .GroupBy(k => k!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static string Format(IReadOnlyDictionary<string, int> tally)
        => tally.Count == 0 ? "-" : string.Join(",", tally.Select(kv => $"{kv.Key}:{kv.Value}"));
}
