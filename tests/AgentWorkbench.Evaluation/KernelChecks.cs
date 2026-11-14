namespace AgentWorkbench.Evaluation;

/// <summary>
/// 本仓的内核判据：每一条都吃**按 RunId 读回来的** <see cref="ExecutionTrace"/>，
/// 判断一次 run 的记录自不自洽。
/// </summary>
/// <remarks>
/// 为什么不用 MAF 内置判定：内置那些读的是对话（调了什么工具、参数、答复文本），
/// 而停因、用量、完整性、截断标志根本不在对话里——它们只在轨迹里。
/// <para>
/// <b>统一约定</b>：<c>trace is null</c> 一律判失败，不跳过、不恒真。
/// 「读不回记录」如果被实现成跳过，整套评测就是假绿——它会在存储挂掉时给出满分。
/// </para>
/// <para>
/// 期望值一律由调用方传字面量，不从被测代码的常量取：从常量取会让「改常量」与「改期望」
/// 同时发生，判据两边一起漂，永远绿。
/// </para>
/// </remarks>
public static class KernelChecks
{
    private const string MissingReason = "没有读回轨迹：记录不存在或读取失败";

    private const string NameOutcome = "终态与归类码";
    private const string NameStop = "结构化停因";
    private const string NameUsage = "本次 run 用量";
    private const string NameComplete = "记录完整性";
    private const string NameTruncated = "工具结果截断标志";
    private const string NameToolSteps = "工具步数";
    private const string NameIterations = "迭代数";

    /// <summary>终态 + 归类码：调用方据此决定重试、降级还是提示用户。</summary>
    public static EvalCheck Outcome(ExecutionTrace? trace, string expectedStatus, string? expectedCode = null)
        => _ => Check(trace, NameOutcome, t =>
        {
            var passed = string.Equals(t.Status, expectedStatus, StringComparison.Ordinal)
                         && string.Equals(t.ErrorCode, expectedCode, StringComparison.Ordinal);
            return passed
                ? (true, $"终态 {t.Status}，归类码 {t.ErrorCode ?? "无"}")
                : (false, $"期望 {expectedStatus} / {expectedCode ?? "无"}，实际 {t.Status} / {t.ErrorCode ?? "无"}");
        });

    /// <summary>停因依据：Kind / 观测值 / 阈值 / 主体四个都要对上。</summary>
    /// <remarks>
    /// 观测值与阈值必须分别比对。循环与迭代场景里两者天然相等（第 N 次触发，N == limit），
    /// 把 Observed 写死成 Limit 也照样绿——只有 token 预算（used &gt; limit）才把两个数字分开。
    /// </remarks>
    public static EvalCheck StopDetail(
        ExecutionTrace? trace, string expectedKind, long expectedObserved, long expectedLimit, string? expectedSubject = null)
        => _ => Check(trace, NameStop, t =>
        {
            if (t.Stop is not { } stop)
                return (false, $"期望停因 {expectedKind}，记录里没有结构化停因");

            var passed = string.Equals(stop.Kind, expectedKind, StringComparison.Ordinal)
                         && stop.Observed == expectedObserved
                         && stop.Limit == expectedLimit
                         && (expectedSubject is null
                             || string.Equals(stop.Subject, expectedSubject, StringComparison.Ordinal));

            return passed
                ? (true, $"停因 {stop.Kind}，观测 {stop.Observed} / 阈值 {stop.Limit}")
                : (false,
                    $"期望 {expectedKind}（观测 {expectedObserved} / 阈值 {expectedLimit}"
                    + (expectedSubject is null ? "" : $" / 主体 {expectedSubject}")
                    + $"），实际 {stop.Kind}（观测 {stop.Observed} / 阈值 {stop.Limit}"
                    + (stop.Subject is null ? "" : $" / 主体 {stop.Subject}") + "）");
        });

    /// <summary>本次 run 的用量：<c>null</c> 表示供应商没上报，与 0 是两回事。</summary>
    /// <remarks>
    /// 这里刻意不接受「进程累计值」冒充本次值：并发跑多个 run 时，累计差值会把别人的 token 记到这次头上。
    /// 期望 <c>null</c> 时实际也必须是 <c>null</c>——否则「供应商没报」与「内核读到了」分不开。
    /// </remarks>
    public static EvalCheck UsagePerRun(ExecutionTrace? trace, long? expectedInput, long? expectedOutput)
        => _ => Check(trace, NameUsage, t =>
        {
            var passed = t.InputTokens == expectedInput && t.OutputTokens == expectedOutput;
            return passed
                ? (true, $"用量 {Fmt(t.InputTokens)} / {Fmt(t.OutputTokens)}")
                : (false, $"期望 {Fmt(expectedInput)} / {Fmt(expectedOutput)}，实际 {Fmt(t.InputTokens)} / {Fmt(t.OutputTokens)}");
        });

    /// <summary>完整性：中断（超时 / 取消）的记录必须自报不完整。</summary>
    public static EvalCheck Completeness(ExecutionTrace? trace, bool expectedComplete)
        => _ => Check(trace, NameComplete, t =>
            t.Complete == expectedComplete
                ? (true, expectedComplete ? "记录自报完整" : "记录自报不完整")
                : (false, $"期望 Complete={expectedComplete}，实际 {t.Complete}"));

    /// <summary>某次迭代的工具步是否带截断标志（结果被截断时必须能看出来）。</summary>
    /// <remarks>
    /// 「找不到该步」判失败，不跳过：跳过会让「工具根本没执行」看起来像「没有截断，合格」。
    /// </remarks>
    public static EvalCheck TruncationFlag(ExecutionTrace? trace, int iteration, bool expectedTruncated)
        => _ => Check(trace, NameTruncated, t =>
        {
            var step = t.Steps.FirstOrDefault(s =>
                s.Iteration == iteration && string.Equals(s.Kind, "tool", StringComparison.Ordinal));

            if (step is null)
                return (false, $"第 {iteration} 轮没有工具步，无法判断截断标志");

            return step.Truncated == expectedTruncated
                ? (true, $"第 {iteration} 轮截断标志 {step.Truncated}")
                : (false, $"期望第 {iteration} 轮 Truncated={expectedTruncated}，实际 {step.Truncated}");
        });

    /// <summary>工具步总数。</summary>
    public static EvalCheck ToolStepCount(ExecutionTrace? trace, int expected)
        => _ => Check(trace, NameToolSteps, t =>
        {
            var actual = t.Steps.Count(s => string.Equals(s.Kind, "tool", StringComparison.Ordinal));
            return actual == expected
                ? (true, $"工具步 {actual}")
                : (false, $"期望 {expected} 个工具步，实际 {actual}");
        });

    /// <summary>迭代数（模型被问了几轮）。</summary>
    public static EvalCheck Iterations(ExecutionTrace? trace, int expected)
        => _ => Check(trace, NameIterations, t =>
            t.Iterations == expected
                ? (true, $"迭代 {t.Iterations}")
                : (false, $"期望迭代 {expected}，实际 {t.Iterations}"));

    private static string Fmt(long? value) => value?.ToString() ?? "未上报";

    /// <summary>统一出口：读不回轨迹一律失败，其余交给具体判据。</summary>
    private static EvalCheckResult Check(
        ExecutionTrace? trace, string name, Func<ExecutionTrace, (bool Passed, string Reason)> evaluate)
    {
        if (trace is null)
            return new EvalCheckResult(false, MissingReason, name);

        var (passed, reason) = evaluate(trace);
        return new EvalCheckResult(passed, reason, name);
    }
}
