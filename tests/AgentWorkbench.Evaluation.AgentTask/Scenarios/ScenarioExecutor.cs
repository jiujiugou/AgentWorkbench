using AgentWorkbench.Evaluation.AgentTask.Checks;
using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using Microsoft.Agents.AI;

namespace AgentWorkbench.Evaluation.AgentTask.Scenarios;

/// <summary>
/// 场景执行器：把声明式 <see cref="TaskScenario"/> 变成「装配一台 harness → 按场景的路径跑一次 → 生成判据」。
///
/// 每个场景各跑一台<b>独立</b> harness（不共享实例）——共享实例会让前一个场景的用量与账本串到后一个，
/// 这与 τ² 的 strict_replay 教训同源：跨任务复用环境必然污染。
/// </summary>
internal static class ScenarioExecutor
{
    /// <summary>只装配、不跑（驱动轨用：把「执行」这一环交给 MAF）。</summary>
    public static HarnessUnderTest Build(TaskScenario scenario)
        => HarnessUnderTest.Create(
            scenario.Script,
            scenario.Loop,
            runtime: scenario.Runtime,
            sessions: scenario.ProbeSessions ? new RecordingSessionStore() : null,
            traceStoreShouldFail: scenario.TraceStoreShouldFail,
            approval: scenario.Approval);

    /// <summary>装配 + 按场景声明的路径跑一次，返回跑完的 harness。</summary>
    public static async Task<HarnessUnderTest> ExecuteAsync(
        TaskScenario scenario, CancellationToken cancellationToken = default)
    {
        var harness = Build(scenario);

        if (scenario.CancelAfter is { } delay)
        {
            // 取消路径：先让 run 停在工具里（外部闸门），再取消——避免靠竞态。
            harness.Tool.HangGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var run = harness.RunExpectingFailureAsync(scenario.Name, cts.Token);
            await Task.Delay(delay, cancellationToken);
            cts.Cancel();
            await run;
        }
        else if (scenario.ExpectFailure)
        {
            await harness.RunExpectingFailureAsync(scenario.Name, cancellationToken);
        }
        else
        {
            await harness.RunAsync(scenario.Name, cancellationToken);
        }

        return harness;
    }

    /// <summary>
    /// 按场景契约生成判据，并逐条<b>限定到本场景</b>。
    ///
    /// 为什么必须限定：批量套件把 N 个场景的判据并成<b>一个数组</b>喂给<b>一个容器</b>；
    /// 若不限定，「期望 succeeded」的判据会顺带去判「期望 failed」的样本，必然误红。
    /// 非本场景样本一律空过（返回通过）——于是每条样本只被自己那份契约判定，
    /// 容器的 Passed/Failed 就直接等于「契约成立的场景数」。
    /// </summary>
    public static EvalCheck[] ChecksFor(
        TaskScenario scenario, IReadOnlyDictionary<string, LedgerFact> ledger, HarnessUnderTest harness)
    {
        var checks = new List<EvalCheck>();

        if (scenario.ExpectStatus is { } status)
            checks.Add(LedgerChecks.StatusIs(ledger, status));
        if (scenario.ExpectErrorCode is { } code)
            checks.Add(LedgerChecks.ErrorCodeIs(ledger, code));
        if (scenario.ExpectNoErrorCode)
            checks.Add(LedgerChecks.ErrorCodeIs(ledger, null));
        if (scenario.ExpectErrorCodeNonEmpty)
            checks.Add(LedgerChecks.ErrorCodeNonEmpty(ledger));
        if (scenario.ExpectPersisted)
            checks.Add(LedgerChecks.WasPersisted(ledger));
        if (scenario.MaxToolSteps is { } maxToolSteps)
            checks.Add(LedgerChecks.ToolStepsAtMost(ledger, maxToolSteps));
        if (scenario.MinToolStepErrors is { } minErrors)
            checks.Add(LedgerChecks.ToolStepErrorsAtLeast(ledger, minErrors));
        if (scenario.ExpectToolsCalled is { } tools)
            checks.Add(EvalChecks.ToolCalledCheck([.. tools]));
        if (scenario.SceneCheck?.Invoke(harness) is { } sceneCheck)
            checks.Add(sceneCheck);

        return checks.Select(check => ScopeTo(scenario.Name, check)).ToArray();
    }

    private static EvalCheck ScopeTo(string scenarioName, EvalCheck inner) => item =>
        string.Equals(item.Query, scenarioName, StringComparison.Ordinal)
            ? inner(item)
            : new EvalCheckResult(true, "非本场景样本，判据空过", $"{scenarioName}/*");
}
