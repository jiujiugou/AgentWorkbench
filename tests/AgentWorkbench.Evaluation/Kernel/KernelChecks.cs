namespace AgentWorkbench.Evaluation.Kernel;

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
    private const string NameSession = "会话归属";
    private const string NameModel = "模型";
    private const string NameElapsed = "耗时";
    private const string NameError = "错误人话";
    private const string NameToolStep = "工具步";
    private const string NamePayload = "工具载荷";
    private const string NameKinds = "步骤序列";

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

    /// <summary>会话归属：重建一次 run 时，它是把轨迹、对话、动作三者关联起来的键。</summary>
    /// <remarks>
    /// 这一条看起来最像「顺便验一下」，实际是最容易串的地方：会话 id 在 Hub 里来自仓储，
    /// 在 Harness 里同时充当 <c>sessionId</c>。任何一处取错（比如用了请求体传来的那个），
    /// 轨迹仍会写成功、字段也都在，只是记到了别人的会话上。
    /// </remarks>
    public static EvalCheck SessionOwnership(ExecutionTrace? trace, string expected)
        => _ => Check(trace, NameSession, t =>
            string.Equals(t.SessionId, expected, StringComparison.Ordinal)
                ? (true, $"会话 {t.SessionId}")
                : (false, $"期望会话 {expected}，实际 {t.SessionId}"));

    /// <summary>用的哪个模型；<c>null</c> 表示本次没用模型（准备阶段就失败），与「用了一个空名」是两回事。</summary>
    public static EvalCheck ModelUsed(ExecutionTrace? trace, string? expected)
        => _ => Check(trace, NameModel, t =>
            string.Equals(t.Model, expected, StringComparison.Ordinal)
                ? (true, $"模型 {t.Model ?? "未使用"}")
                : (false, $"期望模型 {expected ?? "未使用"}，实际 {t.Model ?? "未使用"}"));

    /// <summary>耗时落在区间内。</summary>
    /// <remarks>
    /// 刻意不给「等于多少毫秒」：同一场景两次跑的耗时必然不同，精确断言只会被放宽成
    /// <c>&gt;= 0</c>，那就等于恒真。区间至少能抓到「瞬间返回却报成功」这类明显的假执行。
    /// </remarks>
    public static EvalCheck ElapsedWithin(ExecutionTrace? trace, long minMs, long maxMs = long.MaxValue)
        => _ => Check(trace, NameElapsed, t =>
        {
            var upper = maxMs == long.MaxValue ? "∞" : $"{maxMs}ms";
            var passed = t.LatencyMs >= minMs && t.LatencyMs <= maxMs;
            return passed
                ? (true, $"耗时 {t.LatencyMs}ms 落在 [{minMs}ms,{upper}]")
                : (false, $"期望耗时在 [{minMs}ms,{upper}]，实际 {t.LatencyMs}ms");
        });

    /// <summary>人话错误串与终态一致：失败必须有人说的话，成功必须为空。</summary>
    /// <remarks>
    /// 与 <see cref="Outcome"/> 分开的理由：有归类码不等于有人话。只判码会让「失败但用户看到空白」
    /// 通过评测——而那正是「出错时用户看不出是出错」这个老问题的形态。
    /// </remarks>
    public static EvalCheck ErrorNarrative(ExecutionTrace? trace, bool expectPresent)
        => _ => Check(trace, NameError, t =>
        {
            var present = !string.IsNullOrWhiteSpace(t.Error);
            return present == expectPresent
                ? (true, expectPresent ? $"错误人话：{t.Error}" : "没有人话错误串（成功）")
                : (false, expectPresent ? "期望有一句人话错误，实际为空" : $"期望为空，实际有：{t.Error}");
        });

    /// <summary>某轮工具步的工具名，以及有没有异常（可断言异常文本片段）。</summary>
    /// <remarks>「找不到该步」判失败，不跳过——跳过会让「工具根本没跑」看起来像「工具没出错」。</remarks>
    public static EvalCheck ToolStep(
        ExecutionTrace? trace, int iteration, string expectedTool, string? expectedErrorContains = null)
        => _ => Check(trace, NameToolStep, t =>
        {
            var step = ToolAt(t, iteration);
            if (step is null)
                return (false, $"第 {iteration} 轮没有工具步");

            if (!string.Equals(step.Tool, expectedTool, StringComparison.Ordinal))
                return (false, $"期望工具 {expectedTool}，实际 {step.Tool}");

            if (expectedErrorContains is null)
                return step.Error is null
                    ? (true, $"工具 {step.Tool}，无异常")
                    : (false, $"期望无异常，实际 {step.Error}");

            return step.Error is not null && step.Error.Contains(expectedErrorContains, StringComparison.Ordinal)
                ? (true, $"工具 {step.Tool}，异常含 {expectedErrorContains}")
                : (false, $"期望异常含 {expectedErrorContains}，实际 {step.Error ?? "无异常"}");
        });

    /// <summary>某轮工具步的参数与结果原文：这是「看到了什么」的输入侧与输出侧。</summary>
    public static EvalCheck ToolPayload(
        ExecutionTrace? trace, int iteration, string? argumentsContains = null, string? resultContains = null)
        => _ => Check(trace, NamePayload, t =>
        {
            var step = ToolAt(t, iteration);
            if (step is null)
                return (false, $"第 {iteration} 轮没有工具步");

            if (argumentsContains is not null
                && (step.Arguments is null || !step.Arguments.Contains(argumentsContains, StringComparison.Ordinal)))
                return (false, $"期望参数含 {argumentsContains}，实际 {step.Arguments ?? "无参数"}");

            if (resultContains is not null
                && (step.Result is null || !step.Result.Contains(resultContains, StringComparison.Ordinal)))
                return (false, $"期望结果含 {resultContains}，实际 {step.Result ?? "无结果"}");

            return (true, "参数与结果均符合");
        });

    /// <summary>步骤序列的类型顺序：几轮、每轮是工具还是答复。</summary>
    /// <remarks>
    /// 只比顺序与数量，不比内容——内容归 <see cref="ToolPayload"/>。
    /// 这条在「模型有两条动作入口」之后才有味道：同一次任务走 propose_action 和走直连工具，
    /// 步骤序列的形状不一样，而这个差别正是要能看出来的东西。
    /// </remarks>
    public static EvalCheck StepKinds(ExecutionTrace? trace, params string[] expected)
        => _ => Check(trace, NameKinds, t =>
        {
            var actual = t.Steps.Select(s => s.Kind).ToArray();
            var passed = actual.Length == expected.Length
                         && actual.Zip(expected).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));
            return passed
                ? (true, $"步骤 {string.Join("→", actual)}")
                : (false, $"期望 {string.Join("→", expected)}，实际 {string.Join("→", actual)}");
        });

    private static ExecutionStep? ToolAt(ExecutionTrace trace, int iteration)
        => trace.Steps.FirstOrDefault(s =>
            s.Iteration == iteration && string.Equals(s.Kind, "tool", StringComparison.Ordinal));

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
