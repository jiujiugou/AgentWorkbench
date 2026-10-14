using AgentWorkbench.AgentLoop;
using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.Shared;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Evaluation.AgentTask.Scenarios;

/// <summary>
/// 命名场景目录：把「评估范围」显式列出来 —— 每个场景对应一类任务结局，
/// 合起来覆盖整个任务的行为与结果（正常 / 降级 / 循环 / 预算 / 失败 / 落库故障 / 取消）。
///
/// 全部为<b>合成场景</b>：零业务语义（铁律 #3），零文件、零网络、零外部 provider（《标准》§5）。
/// 期望值全部是技术契约（状态码 / 归类码 / 步数），不是某个业务域的知识。
/// </summary>
internal static class TaskScenarios
{
    /// <summary>基准执行控制旋钮：不设 token 预算、不做重复检测。</summary>
    public static AgentLoopOptions BaseLoop
        => new() { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 0 };

    private static ChatResponse ToolCall(string mode, int failTimes = 0, int delayMs = 0)
        => ScriptedResponses.ToolCall(
            FaultInjectorTool.ToolName,
            ScriptedResponses.Args(("mode", mode), ("failTimes", failTimes), ("delayMs", delayMs)));

    /// <summary>正常收口：模型调一次工具、再给出最终答复。</summary>
    public static TaskScenario HappyPath => new()
    {
        Name = "正常收口：调一次工具再答完",
        Family = "正常收口",
        Script = call => call == 1 ? ToolCall("throw") : ScriptedResponses.Text("done"),
        ExpectStatus = AgentRunStatus.Succeeded,
        ExpectNoErrorCode = true,
        ExpectToolsCalled = [FaultInjectorTool.ToolName]
    };

    /// <summary>正常收口（降级）：模型直接作答、不调工具——与上一条共用判据时必须能区分。</summary>
    public static TaskScenario DegradedNoTool => new()
    {
        Name = "降级：模型直接答、不调工具",
        Family = "正常收口",
        Script = _ => ScriptedResponses.Text("degraded"),
        ExpectStatus = AgentRunStatus.Succeeded,
        ExpectNoErrorCode = true,
        MaxToolSteps = 0
    };

    /// <summary>终止：同一工具同一参数反复，被重复检测拦在执行之前（但已记账）。</summary>
    public static TaskScenario LoopDetected => new()
    {
        Name = "循环：同一调用反复到上限",
        Family = "终止",
        Script = _ => ToolCall("throw"),
        Loop = new AgentLoopOptions { MaxIterations = 10, TimeoutSeconds = 30, MaxRepeatedToolCalls = 3 },
        ExpectStatus = AgentRunStatus.LoopDetected,
        ExpectErrorCode = AgentRunCode.LoopDetected,
        MaxToolSteps = 3,
        MinToolStepErrors = 1
    };

    /// <summary>预算：墙钟管不住钱——token 烧穿 run 级预算即终止。</summary>
    public static TaskScenario TokenBudgetExhausted => new()
    {
        Name = "预算：token 烧穿上限",
        Family = "预算",
        Script = _ => ToolCall("throw"),
        Runtime = new MeteredModelRuntime(new UsageMeter()) { TokensPerRequest = 100 },
        Loop = new AgentLoopOptions
        {
            MaxIterations = 10,
            TimeoutSeconds = 30,
            MaxRepeatedToolCalls = 0,
            MaxTokensPerRun = 250
        },
        ExpectStatus = AgentRunStatus.BudgetExceeded,
        ExpectErrorCode = AgentRunCode.BudgetExceeded
    };

    /// <summary>失败：模型侧非工具异常 → 归 unhandled_error。</summary>
    public static TaskScenario UnhandledError => new()
    {
        Name = "失败：模型侧异常归 unhandled_error",
        Family = "失败",
        Script = _ => throw new InvalidOperationException("模型侧崩溃（评测场景）"),
        ExpectFailure = true,
        ExpectStatus = AgentRunStatus.Failed,
        ExpectErrorCode = AgentRunCode.Unhandled,
        ExpectErrorCodeNonEmpty = true
    };

    /// <summary>失败：账本写不进去，但冒泡的必须仍是原始异常（账本故障不得顶替）。</summary>
    public static TaskScenario LedgerWriteFails => new()
    {
        Name = "失败：账本写失败不掩盖原始异常",
        Family = "失败",
        Script = _ => throw new InvalidOperationException("模型侧崩溃（评测场景）"),
        TraceStoreShouldFail = true,
        ExpectFailure = true,
        ExpectPersisted = false
    };

    /// <summary>终止：调用方中途取消 → cancelled，且会话仍写回（令牌不可取消）。</summary>
    public static TaskScenario CallerCancellation => new()
    {
        Name = "取消：调用方中途取消",
        Family = "终止",
        Script = _ => ToolCall("hang"),
        CancelAfter = TimeSpan.FromMilliseconds(50),
        ProbeSessions = true,
        ExpectFailure = true,
        ExpectStatus = AgentRunStatus.Cancelled,
        ExpectErrorCode = AgentRunCode.Cancelled,
        SceneCheck = harness => _ =>
            harness.Sessions is { Saves.Count: > 0 } && harness.Sessions.Saves.All(save => !save.TokenCanBeCanceled)
                ? new EvalCheckResult(true, "取消时会话写回且令牌不可取消", "scenario.session_uncancellable")
                : new EvalCheckResult(false, "取消时必须写回会话、且令牌不可取消", "scenario.session_uncancellable")
    };

    /// <summary>全部场景 = 批量套件的评估范围。</summary>
    public static IReadOnlyList<TaskScenario> All =>
    [
        HappyPath,
        DegradedNoTool,
        LoopDetected,
        TokenBudgetExhausted,
        UnhandledError,
        LedgerWriteFails,
        CallerCancellation
    ];
}
