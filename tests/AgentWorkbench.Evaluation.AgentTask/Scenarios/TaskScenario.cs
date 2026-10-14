using AgentWorkbench.AgentLoop;
using AgentWorkbench.Evaluation.AgentTask.Fixtures;
using AgentWorkbench.ModelRuntime;
using AgentWorkbench.ToolExecution;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Evaluation.AgentTask.Scenarios;

/// <summary>
/// 一个「任务场景」= 评估范围的最小单元。三件事声明式给全：
/// <list type="number">
///   <item>怎么装配 —— 模型脚本 + 执行控制旋钮 + 替身（<see cref="Runtime"/> / <see cref="Approval"/> /
///         <see cref="TraceStoreShouldFail"/> / <see cref="ProbeSessions"/>）；</item>
///   <item>怎么跑 —— 正常收口 / 期望抛出（<see cref="ExpectFailure"/>）/ 中途取消（<see cref="CancelAfter"/>）；</item>
///   <item>期望什么 —— 终态契约（<see cref="ExpectStatus"/> 等），是<b>数据</b>而不是代码。</item>
/// </list>
/// 判据由 <see cref="ScenarioExecutor.ChecksFor"/> 按契约自动生成，全部落在账本上。
/// 场景名同时用作 run 的输入文本与账本 / 判据的对齐键，因此必须唯一。
/// </summary>
internal sealed record TaskScenario
{
    /// <summary>场景名（唯一）：既是喂给 harness 的输入文本，也是账本与判据的对齐键。</summary>
    public required string Name { get; init; }

    /// <summary>模型脚本：按模型请求序号（从 1 起）返回预设响应；可直接抛异常模拟模型侧故障。</summary>
    public required Func<int, ChatResponse> Script { get; init; }

    /// <summary>场景族（报告分组用）。</summary>
    public string Family { get; init; } = "未分组";

    /// <summary>执行控制旋钮（迭代 / 墙钟 / 重复检测 / token 预算）。</summary>
    public AgentLoopOptions Loop { get; init; } = TaskScenarios.BaseLoop;

    /// <summary>模型运行时替身；要给 token 预算就得传挂了共享 <c>UsageMeter</c> 的 <c>MeteredModelRuntime</c>。</summary>
    public IModelRuntime? Runtime { get; init; }

    /// <summary>审批策略；默认全部 Auto（与生产 appsettings 默认一致）。</summary>
    public IToolApprovalPolicy? Approval { get; init; }

    /// <summary>置真模拟账本写入故障（验证「落库失败不掩盖原始异常」）。</summary>
    public bool TraceStoreShouldFail { get; init; }

    /// <summary>置真时装配会话探针，可断言「取消时会话仍写回」。</summary>
    public bool ProbeSessions { get; init; }

    /// <summary>本场景以「抛出」收场（失败 / 取消 / 超时三条路径，走 RunExpectingFailureAsync）。</summary>
    public bool ExpectFailure { get; init; }

    /// <summary>非空时：发起 run 后等这段时间再取消（走「调用方取消」路径）。</summary>
    public TimeSpan? CancelAfter { get; init; }

    // ---------- 终态契约（数据） ----------

    /// <summary>账本 status 必须等于它；<c>null</c> = 不判。</summary>
    public string? ExpectStatus { get; init; }

    /// <summary>账本 error_code 必须等于它；<c>null</c> = 不判。</summary>
    public string? ExpectErrorCode { get; init; }

    /// <summary>置真断言「不得有归类码」（正常收口场景）。</summary>
    public bool ExpectNoErrorCode { get; init; }

    /// <summary>置真断言「归类码非空」（失败路径必须能把原因说清楚）。</summary>
    public bool ExpectErrorCodeNonEmpty { get; init; }

    /// <summary>是否要求本次 run 落了账（默认要求）。</summary>
    public bool ExpectPersisted { get; init; } = true;

    /// <summary>工具步数上限。</summary>
    public int? MaxToolSteps { get; init; }

    /// <summary>带 error 的工具步数下限。</summary>
    public int? MinToolStepErrors { get; init; }

    /// <summary>必须被调用到的工具名（走 MAF 的对话侧判据 <c>EvalChecks.ToolCalledCheck</c>）。</summary>
    public IReadOnlyList<string>? ExpectToolsCalled { get; init; }

    /// <summary>场景级自定义判据（读账本之外的观测量，如会话写回令牌）；<c>null</c> = 无。</summary>
    public Func<HarnessUnderTest, EvalCheck?>? SceneCheck { get; init; }
}
