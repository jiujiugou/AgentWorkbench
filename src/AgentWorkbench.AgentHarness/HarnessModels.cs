namespace AgentWorkbench.AgentHarness;

/// <summary>Harness 能力位（供前端/探活）。</summary>
public sealed record HarnessCapabilities(bool Loop, bool Llm, bool Context, bool Memory, IReadOnlyList<string> Tools);

/// <summary>一次 Harness run 请求。</summary>
public sealed record HarnessRunRequest(string? SessionId, string Input, bool UseKnowledge = true);

/// <summary>单步轨迹（loop 内的一次工具调用或最终答复）。</summary>
public sealed record HarnessStep(int Iteration, string Kind, string? Tool, string? Arguments, string? Result);

/// <summary>一次 Harness run 结果。</summary>
public sealed record HarnessRunResult(
    long RunId, string SessionId, string Text, int Iterations, string? Model, long LatencyMs,
    IReadOnlyList<HarnessStep> Steps);

/// <summary>未配置模型，Harness 无法运行。</summary>
public sealed class HarnessModelNotConfiguredException()
    : Exception("未配置模型，无法运行 Agent Harness");
