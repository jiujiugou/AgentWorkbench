namespace AgentWorkbench.Evaluation.Kernel;

/// <summary>
/// 一次采样的结论：<b>只装数字与结论，不装理由</b>。
/// </summary>
/// <remarks>
/// 与 <see cref="KernelChecks"/> 的分工：判据回答「这一次守没守住」（带失败理由），
/// 采样回答「这一次的量是多少」（可被聚合）。理由留在 <c>AgentEvaluationResults</c> 里，
/// 这里只保留能参与统计的那几个值。
///
/// <b>为什么要有 <see cref="HasTrace"/></b>：读不回轨迹时判据全红（这是对的），
/// 但它的迭代数 / 耗时 / 用量全是 0。那些 0 一旦进入均值，会同时拉低平均轮数、
/// 拉低平均耗时，让「存储挂了」看起来像「跑得又快又省」。所以聚合时必须把它们排除在数字统计之外，
/// 只在 <see cref="KernelReport.MissingTrace"/> 与通过率里体现。
/// </remarks>
/// <param name="RunId">哪一次 run；排障时靠它回到原始轨迹。</param>
/// <param name="AllPassed">本次判据是否全部通过。<c>false</c> 也可能因为「轨迹缺失」。</param>
/// <param name="HasTrace">是否读到了轨迹。为 <c>false</c> 时其余数字字段无意义。</param>
public sealed record KernelSample(
    long RunId,
    bool AllPassed,
    bool HasTrace,
    int Iterations,
    int ToolSteps,
    long LatencyMs,
    string Status,
    string? StopKind,
    long? InputTokens,
    long? OutputTokens)
{
    /// <summary>
    /// 从一次 run 的产物与判定结果构造采样。
    /// </summary>
    /// <remarks>
    /// 数字一律取 <paramref name="trace"/>（落库的事实），不取 <paramref name="run"/>（内存对象）。
    /// 两者在正常情况下一致，但只有前者能证明「事后重建得出来」。
    /// </remarks>
    public static KernelSample From(
        HarnessRunResult run, ExecutionTrace? trace, AgentEvaluationResults results)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(results);

        if (trace is null)
        {
            // 轨迹缺失：数字不参与均值，但仍算一次采样、仍计入失败。
            return new(run.RunId, results.AllPassed, false, 0, 0, 0, "unknown", null, null, null);
        }

        return new(
            run.RunId,
            results.AllPassed,
            true,
            trace.Iterations,
            trace.Steps.Count(s => string.Equals(s.Kind, "tool", StringComparison.Ordinal)),
            trace.LatencyMs,
            trace.Status,
            trace.Stop?.Kind,
            trace.InputTokens,
            trace.OutputTokens);
    }
}
