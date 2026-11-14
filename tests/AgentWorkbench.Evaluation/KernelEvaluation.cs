namespace AgentWorkbench.Evaluation;

/// <summary>
/// 评测入口：把一次 run 的结果投影进 MAF，交给 <see cref="LocalEvaluator"/> 跑判定并聚合。
/// </summary>
/// <remarks>
/// 刻意很薄，不自建 runner：
/// <list type="bullet">
/// <item>判定机制、结果聚合、<c>AssertAllPassed</c> 全是 MAF 给的（<c>LocalEvaluator</c> + <c>AgentEvaluationResults</c>）；</item>
/// <item>题库运行、重复次数、多评测器并列也在框架里，本类不重复实现；</item>
/// <item>本类只解决「框架看不见轨迹」这一件事——把 RunId 换成 <see cref="ExecutionTrace"/> 再交给判据闭包。</item>
/// </list>
/// <para>
/// 为什么不直接用 <c>AIAgent.EvaluateAsync</c>：那个入口要一个 <c>AIAgent</c>，而本仓的 agent
/// 在 Harness 的私有装配里。为评测造一个 agent 等于让评测跑另一次生成——评的就不是刚跑完的那次 run 了。
/// </para>
/// </remarks>
public static class KernelEvaluation
{
    /// <summary>用调用方已拿到的轨迹评测（轨迹由调用方负责读，评测只负责判）。</summary>
    /// <param name="result">一次 run 的产物（步骤序列会投影成对话）。</param>
    /// <param name="query">本次输入，进 <see cref="EvalItem.Query"/>。</param>
    /// <param name="trace">按 <c>result.RunId</c> 读回来的轨迹；<c>null</c> 时所有判据判失败。</param>
    /// <param name="checks">判据工厂：拿到轨迹后产出本次要跑的判据集合。</param>
    public static async Task<AgentEvaluationResults> EvaluateAsync(
        HarnessRunResult result,
        string query,
        ExecutionTrace? trace,
        Func<ExecutionTrace?, IEnumerable<EvalCheck>> checks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(checks);

        var item = new EvalItem(query, result.Text, HarnessResponseProjection.ToConversation(result));
        var evaluator = new LocalEvaluator([.. checks(trace)]);
        return await evaluator.EvaluateAsync([item], $"run-{result.RunId}", cancellationToken);
    }

    /// <summary>按 <c>result.RunId</c> 从读口取轨迹再评测（闭环：评的是落库之后的事实）。</summary>
    /// <remarks>
    /// 这是「内核产出了 → 应用层收得到」的唯一一条可执行验证：如果落库丢字段，
    /// 判据读到的是 <c>null</c>，会红；如果读口返回 <c>null</c>，也会红。
    /// </remarks>
    public static async Task<AgentEvaluationResults> EvaluateAsync(
        HarnessRunResult result,
        string query,
        IExecutionTraceReader reader,
        Func<ExecutionTrace?, IEnumerable<EvalCheck>> checks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(reader);

        var trace = await reader.GetAsync(result.RunId, cancellationToken);
        return await EvaluateAsync(result, query, trace, checks, cancellationToken);
    }
}
