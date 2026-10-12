namespace AgentWorkbench.AgentHarness;

/// <summary>Harness 能力位（供前端/探活）。</summary>
public sealed record HarnessCapabilities(bool Loop, bool Llm, bool Context, bool Memory, IReadOnlyList<string> Tools);

/// <summary>一次 Harness run 请求。</summary>
public sealed record HarnessRunRequest(string? SessionId, string Input, bool UseKnowledge = true);

/// <summary>单步轨迹（loop 内的一次工具调用或最终答复）。</summary>
/// <remarks><see cref="Error"/> 仅工具步可能非空：调用失败时记异常类型与消息，便于回放时区分「失败」与「空结果」。</remarks>
public sealed record HarnessStep(
    int Iteration, string Kind, string? Tool, string? Arguments, string? Result, string? Error = null);

/// <summary>
/// 一次 Harness run 结果。
/// </summary>
/// <remarks>
/// <see cref="Status"/> 说明「以什么状态结束」（取值见 <see cref="AgentRunStatus"/>：正常收尾 / 跑满迭代 /
/// 循环 / 超预算 / 超时 / 取消 / 失败），<see cref="Code"/> 说明「为什么」（取值见 <see cref="AgentRunCode"/>）。
/// 只回一个 <see cref="Text"/> 时，「跑满迭代被强制收口」与「模型答完了」在结果里长得一模一样。
/// </remarks>
public sealed record HarnessRunResult(
    long RunId, string SessionId, string Text, int Iterations, string? Model, long LatencyMs,
    IReadOnlyList<HarnessStep> Steps, string Status, string? Code = null);

/// <summary>未配置模型，Harness 无法运行。</summary>
public sealed class HarnessModelNotConfiguredException()
    : Exception("未配置模型，无法运行 Agent Harness");
