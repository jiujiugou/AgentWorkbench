using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.Observability;
using Microsoft.Agents.AI;

namespace AgentWorkbench.Evaluation.AgentTask.Checks;

/// <summary>
/// 自建判定原语（对应《评测实现指南》§2 第 10–16 行：判据全部自建，MAF 只出容器）。
///
/// 为什么要有这一层：<c>EvalChecks.*</c> 只能看见对话（工具调用、文本），
/// 而本仓库最有价值的终态信息在**账本**里（status / error_code / steps）——
/// 「跑满迭代」与「答完了」在对话上长得一模一样，只有账本能分开。
/// 所以判据必须能读到账本：<c>EvalItem</c> 带 <c>Query</c>，用它把样本映射回本轮落库的事实。
/// </summary>
internal static class LedgerChecks
{
    /// <summary>终态必须是 <paramref name="expected"/>（可证伪：把期望值换成另一种终态，本检查必须红）。</summary>
    public static EvalCheck StatusIs(IReadOnlyDictionary<string, LedgerFact> ledger, string expected)
        => item => Fact(ledger, item) is { } fact
            ? new EvalCheckResult(
                string.Equals(fact.Status, expected, StringComparison.Ordinal),
                $"账本 status={Show(fact.Status)}，期望 {expected}",
                $"ledger.status_is({expected})")
            : Missing(item);

    /// <summary>归类码必须是 <paramref name="expected"/>；<c>null</c> 期望表示「不该有归类码」。</summary>
    public static EvalCheck ErrorCodeIs(IReadOnlyDictionary<string, LedgerFact> ledger, string? expected)
        => item => Fact(ledger, item) is { } fact
            ? new EvalCheckResult(
                string.Equals(fact.ErrorCode, expected, StringComparison.Ordinal),
                $"账本 error_code={Show(fact.ErrorCode)}，期望 {Show(expected)}",
                $"ledger.error_code_is({Show(expected)})")
            : Missing(item);

    /// <summary>失败 / 取消 / 超时路径的归类码**不得为空**——只留人话，调用方无法决定重试还是降级。</summary>
    public static EvalCheck ErrorCodeNonEmpty(IReadOnlyDictionary<string, LedgerFact> ledger)
        => item => Fact(ledger, item) is { } fact
            ? new EvalCheckResult(
                !string.IsNullOrEmpty(fact.ErrorCode),
                $"账本 error_code={Show(fact.ErrorCode)}，不得为空",
                "ledger.error_code_non_empty")
            : Missing(item);

    /// <summary>本次 run 确实落了账（有轨迹行）。仓储被注入故障时该检查变红。</summary>
    public static EvalCheck WasPersisted(IReadOnlyDictionary<string, LedgerFact> ledger)
        => item => Fact(ledger, item) is { } fact
            ? new EvalCheckResult(fact.Persisted, "账本里必须留下这次 run", "ledger.persisted")
            : Missing(item);

    /// <summary>不得落 assistant 步：提前终止时模型往往没有答复，落一条空步会把「没有答复」写成「答复为空」。</summary>
    public static EvalCheck NoAssistantStep(IReadOnlyDictionary<string, LedgerFact> ledger)
        => item => Fact(ledger, item) is { } fact
            ? new EvalCheckResult(
                !fact.Steps.Any(step => string.Equals(step.Kind, "assistant", StringComparison.Ordinal)),
                $"账本步骤={Describe(fact.Steps)}，不应出现 assistant 步",
                "ledger.no_assistant_step")
            : Missing(item);

    /// <summary>最终答复步的长度不得超过 <paramref name="max"/>（轨迹只用于回放/排障，不做全文归档）。</summary>
    public static EvalCheck AssistantStepWithin(IReadOnlyDictionary<string, LedgerFact> ledger, int max)
        => item => Fact(ledger, item) is { } fact
            ? fact.Steps.LastOrDefault(step => string.Equals(step.Kind, "assistant", StringComparison.Ordinal)) is { } step
                ? new EvalCheckResult(
                    (step.Result?.Length ?? 0) <= max,
                    $"assistant 步长度={step.Result?.Length ?? 0}，上限 {max}",
                    $"ledger.assistant_step_within({max})")
                : new EvalCheckResult(false, "账本里没有 assistant 步", $"ledger.assistant_step_within({max})")
            : Missing(item);

    /// <summary>工具步数上限：用于证明「预算破了之后没有继续烧」（拦在执行之前，而不是事后追认）。</summary>
    public static EvalCheck ToolStepsAtMost(IReadOnlyDictionary<string, LedgerFact> ledger, int max)
        => item => Fact(ledger, item) is { } fact
            ? new EvalCheckResult(
                CountTools(fact) <= max,
                $"工具步数={CountTools(fact)}，上限 {max}",
                $"ledger.tool_steps_at_most({max})")
            : Missing(item);

    /// <summary>至少 <paramref name="min"/> 个工具步带 error：工具失败的 run 必须在轨迹里留得下「哪一步炸了」。</summary>
    public static EvalCheck ToolStepErrorsAtLeast(IReadOnlyDictionary<string, LedgerFact> ledger, int min)
        => item => Fact(ledger, item) is { } fact
            ? new EvalCheckResult(
                fact.Steps.Count(step => step.Kind == "tool" && !string.IsNullOrEmpty(step.Error)) >= min,
                $"带 error 的工具步数={fact.Steps.Count(step => step.Kind == "tool" && !string.IsNullOrEmpty(step.Error))}，下限 {min}",
                $"ledger.tool_step_errors_at_least({min})")
            : Missing(item);

    /// <summary>一次都没跑过的 query（夹具与判据错位）不算通过——宁可红，也不要静默放行。</summary>
    private static EvalCheckResult Missing(EvalItem item)
        => new(false, $"账本里没有 query=“{item.Query}”对应的 run（判据与样本错位）", "ledger.sample_present");

    private static LedgerFact? Fact(IReadOnlyDictionary<string, LedgerFact> ledger, EvalItem item)
        => ledger.TryGetValue(item.Query, out var fact) ? fact : null;

    private static int CountTools(LedgerFact fact)
        => fact.Steps.Count(step => string.Equals(step.Kind, "tool", StringComparison.Ordinal));

    private static string Show(string? value) => value is null ? "<null>" : value;

    private static string Describe(IEnumerable<ExecutionStep> steps)
        => "[" + string.Join(", ", steps.Select(step => $"{step.Iteration}:{step.Kind}")) + "]";
}
