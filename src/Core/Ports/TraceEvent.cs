namespace AgentWorkbench.Ports;

/// <summary>
/// 审计事件结构（注入口 3）。工程保障层只提供结构与落库通道，不产生业务内容。
/// 审计五字段：<c>Intent</c> / <c>Arguments</c> / <c>Approver</c> / <c>Result</c> / <c>ExternalId</c>，
/// 分别由流程步骤 2/3/4/5/6 产生，缺一即视为轨迹不完整（M1 设计 §6）。
/// </summary>
/// <param name="Stage">取自 <see cref="AuditStages"/>，标明是哪一道校验或执行阶段。</param>
/// <param name="Outcome">该阶段结果：allow / deny / written / executed 等，由实现给值。</param>
/// <param name="Intent">意图，来自已校验的 <see cref="ActionRequest.ActionKey"/>。</param>
/// <param name="Arguments">参数快照，来自 <see cref="ActionRequest.Arguments"/> 的原始 JSON 文本。</param>
/// <param name="Approver">审批人；无审批时为 null，但不得伪造。</param>
/// <param name="Result">阶段结果摘要。</param>
/// <param name="ExternalId">外部单号；无外部副作用时为 null。</param>
public sealed record TraceEvent(
    string RequestId,
    string Stage,
    string Outcome,
    string Intent,
    string Arguments,
    string? Approver,
    string? Result,
    string? ExternalId,
    DateTimeOffset At);

/// <summary>
/// 五字段错误信封的<b>类型名占位</b>。ADR 0017 §二 提到该信封，§三 未列出字段构成；
/// M1 设计 §3.3 明确「不臆造」，故此处只保留名字，字段待 ADR 补齐后再定。
/// 当前不含任何字段，禁止在实现里绕过它自行拼装错误响应。
/// </summary>
public sealed record ErrorEnvelope;
