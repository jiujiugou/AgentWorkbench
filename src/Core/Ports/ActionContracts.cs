namespace AgentWorkbench.Ports;

/// <summary>
/// 动作类型声明。类型定义在底座，实例声明在动作域（ADR 0017 §三）。
/// <paramref name="Arguments"/> 的字段含义底座一律不理解，只做透传与留痕。
///
/// 除四个位置参数外，其余字段是 <b>业务能力目录</b>（方案第 3 步）的元数据：
/// 由动作域填写、底座只透传、不解读、不据此做决策。用 required init 强制每个域补齐，
/// 避免出现「有 ActionKey 但没有描述 / 没有参数 Schema」的半成品目录。
/// </summary>
/// <param name="ActionKey">动作域内唯一标识，底座只当字符串比较。</param>
/// <param name="SchemaId">指向动作域提供的 JSON Schema；第 1 道校验用它判参数是否合法。</param>
/// <param name="SideEffecting">true = 有外部副作用，必须登记幂等键；<b>不再决定是否审批</b>。</param>
/// <param name="Primitive">取自 <see cref="ActionPrimitives"/> 的四个原语之一。</param>
public sealed record ActionSpec(
    string ActionKey,
    string SchemaId,
    bool SideEffecting,
    string Primitive)
{
    /// <summary>给人看的名字。审批单、执行预览、审计里显示；底座不理解内容。</summary>
    public required string DisplayName { get; init; }

    /// <summary>给模型看：什么时候该用这个能力、参数含义是什么。底座不理解内容。</summary>
    public required string Description { get; init; }

    /// <summary>
    /// 风险分级，取自 <see cref="RiskLevels"/>。
    /// <b>仅供展示、排序和管理后台分类；禁止参与任何放行/拒绝决策。</b>
    /// 决策只看 <see cref="RequiresApproval"/> 与动作域给出的 PDP/业务规则结论。
    /// 底座若按 risk 字符串判审批，就等于理解了风险分级，换域时会错（AC-01）。
    /// </summary>
    public required string RiskLevel { get; init; }

    /// <summary>
    /// 是否需要人工审批。与 <see cref="SideEffecting"/> 独立：
    /// 「有副作用但免审批」（如低风险草稿）与「无副作用但需审批」都能表达。
    /// </summary>
    public required bool RequiresApproval { get; init; }

    /// <summary>契约版本。外部系统字段漂移时可据此识别是哪一版目录。</summary>
    public required string Version { get; init; }

    /// <summary>
    /// 参数的 JSON Schema 原文。底座不解析、不校验业务字段，只原样透传给工具面与 Schema 校验器。
    /// 这是「第 3 步 → 第 4 步」能被打通的关键：没有它，模型不知道该传什么。
    /// </summary>
    public required string InputSchemaJson { get; init; }
}

/// <summary>风险等级取值。<b>只用于展示/排序，不是决策枚举</b>，见 <see cref="ActionSpec.RiskLevel"/>。</summary>
public static class RiskLevels
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";
}

/// <summary>四个原语。动作类型，不是一次 run 的动作清单（后者见 M1 设计 §10 待确认 3）。</summary>
public static class ActionPrimitives
{
    public const string Query = "Query";
    public const string Draft = "Draft";
    public const string Submit = "Submit";
    public const string Cancel = "Cancel";

    public static readonly IReadOnlyList<string> All = [Query, Draft, Submit, Cancel];
}

/// <summary>
/// 一次动作请求。<c>RequestId</c> 从入口贯穿到外部单号回写与所有轨迹（M1 设计 §6）。
/// </summary>
/// <param name="Arguments">对底座不透明的 JSON；底座不解析、不校验业务字段、不据此决策。</param>
/// <param name="IdempotencyKey">与 RequestId 同源；有副作用时必填，去重表在底座侧。</param>
public sealed record ActionRequest(
    string RequestId,
    string Subject,
    string ActionKey,
    JsonElement Arguments,
    string? IdempotencyKey);

/// <summary>动作终态。<c>Status</c> / <c>Code</c> 为字符串常量，取值见 <see cref="ActionStatuses"/>。</summary>
/// <param name="ExternalId">外部系统单号；只有真正产生外部副作用后才有值。</param>
public sealed record ActionResult(
    string RequestId,
    string Status,
    string Code,
    string? ExternalId,
    string? ErrorCode,
    JsonElement Data);

/// <summary>
/// 终态字符串。与 <c>AgentWorkbench.Shared.Execution.AgentRunStatus/Code</c> 同源，
/// 但本工程按 M1 设计 §7 门禁 4 保持零 ProjectReference，故不引用那些类型，
/// 由网关/组合根负责取值对齐 —— 这处张力见 README「待裁决」。
/// <c>InProgress</c> / <c>Pending</c> / <c>Timeout</c> 用于超时查证的状态记录；
/// 流程暂停与续跑属 run 状态机，是 §9 第 4 步的契约，不在本块定义。
/// </summary>
public static class ActionStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    // Validated 是四道校验的通过态，非终态：表示「允许进入后续审批 / 执行」，不代表动作已成功。
    public const string Validated = "validated";
    public const string InProgress = "in_progress";
    public const string Pending = "pending";
    public const string Timeout = "timeout";
    public const string Unknown = "unknown";
}
