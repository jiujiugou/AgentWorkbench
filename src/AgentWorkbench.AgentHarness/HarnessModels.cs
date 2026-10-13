namespace AgentWorkbench.AgentHarness;

/// <summary>Harness 能力位（供前端/探活）。</summary>
/// <param name="Loop">执行控制（迭代预算 / run 级超时）是否就绪。</param>
/// <param name="Llm">是否解析到可用的模型端点——为 <c>false</c> 时无法运行。</param>
/// <param name="Context">上下文/RAG 组装是否启用。</param>
/// <param name="Memory">会话（跨 run 记忆）是否启用。</param>
/// <param name="Tools">当前白名单下可用的工具名。</param>
public sealed record HarnessCapabilities(bool Loop, bool Llm, bool Context, bool Memory, IReadOnlyList<string> Tools);

/// <summary>一次 Harness run 请求。</summary>
/// <param name="SessionId">会话标识；为空时本次调用使用随机会话（无跨 run 记忆）。</param>
/// <param name="Input">用户输入原文。知识检索不再由 harness 暗做——模型需要依据时调 <c>search_knowledge</c> 工具，轨迹留名。</param>
/// <param name="Instructions">
/// 本次 run 的系统指令（人格 / 约束，如知识问答的引用规范）。经 <c>RunInstructionsChatClient</c>
/// 装饰在工具环外面注入：只往模型请求前插一条 system 消息，不写进会话历史
/// （否则同一指令会随每轮对话重复累积）。<b>不要改用 Agent 的 AIContextProviders</b>——
/// 那会让 MEA 在我们装配的函数调用环外再包一层，绕过守卫与轨迹采集。
/// </param>
public sealed record HarnessRunRequest(string? SessionId, string Input, string? Instructions = null);

/// <summary>单步轨迹（loop 内的一次工具调用或最终答复）。</summary>
/// <param name="Iteration">所在迭代轮次，从 0 起。</param>
/// <param name="Kind">步骤类型，当前取值为 <c>tool</c> 或 <c>assistant</c>。</param>
/// <param name="Tool">工具名；仅工具步非空。</param>
/// <param name="Arguments">工具调用参数的序列化文本。</param>
/// <param name="Result">工具返回值或（截断后的）最终答复文本。</param>
/// <param name="Error">异常类型+消息；仅工具步可能非空。</param>
/// <remarks><see cref="Error"/> 仅工具步可能非空：调用失败时记异常类型与消息，便于回放时区分「失败」与「空结果」。</remarks>
public sealed record HarnessStep(
    int Iteration, string Kind, string? Tool, string? Arguments, string? Result, string? Error = null);

/// <summary>
/// 一次 Harness run 结果。
/// </summary>
/// <param name="RunId">本次 run 的轨迹标识，可用于 <see cref="IAgentHarness.GetRunAsync"/> 回放。</param>
/// <param name="SessionId">实际使用的会话标识（请求未带时此处为生成的随机值）。</param>
/// <param name="Text">模型最终答复文本（提前终止时可能为空）。</param>
/// <param name="Iterations">实际迭代轮数。</param>
/// <param name="Model">本次使用的模型名；模型未配置时为 <c>null</c>。</param>
/// <param name="LatencyMs">整条 run 的墙钟耗时（毫秒）。</param>
/// <param name="Steps">按顺序排列的步骤轨迹。</param>
/// <param name="Status">结束状态，取值见 <see cref="AgentRunStatus"/>。</param>
/// <param name="Code">结束原因码，取值见 <see cref="AgentRunCode"/>；正常收尾时为 <c>null</c>。</param>
/// <remarks>
/// <see cref="Status"/> 说明「以什么状态结束」（取值见 <see cref="AgentRunStatus"/>：正常收尾 / 跑满迭代 /
/// 循环 / 超预算 / 超时 / 取消 / 失败），<see cref="Code"/> 说明「为什么」（取值见 <see cref="AgentRunCode"/>）。
/// 只回一个 <see cref="Text"/> 时，「跑满迭代被强制收口」与「模型答完了」在结果里长得一模一样。
/// </remarks>
public sealed record HarnessRunResult(
    long RunId, string SessionId, string Text, int Iterations, string? Model, long LatencyMs,
    IReadOnlyList<HarnessStep> Steps, string Status, string? Code = null);

/// <summary>未配置模型，Harness 无法运行（调用方据此按 <see cref="AgentRunCode.ModelUnavailable"/> 区分处理）。</summary>
public sealed class HarnessModelNotConfiguredException()
    : Exception("未配置模型，无法运行 Agent Harness");
