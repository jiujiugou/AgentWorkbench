namespace AgentWorkbench.Ports;

/// <summary>
/// 动作类型声明。类型定义在底座，实例声明在动作域（ADR 0017 §三）。
/// <paramref name="Arguments"/> 的字段含义底座一律不理解，只做透传与留痕。
/// </summary>
/// <param name="ActionKey">动作域内唯一标识，底座只当字符串比较。</param>
/// <param name="SchemaId">指向动作域提供的 JSON Schema；第 1 道校验用它判参数是否合法。</param>
/// <param name="SideEffecting">true = 有外部副作用，必须登记幂等键并走审批；false = 可重复执行。</param>
/// <param name="Primitive">取自 <see cref="ActionPrimitives"/> 的四个原语之一。</param>
public sealed record ActionSpec(
    string ActionKey,
    string SchemaId,
    bool SideEffecting,
    string Primitive);

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
    public const string InProgress = "in_progress";
    public const string Pending = "pending";
    public const string Timeout = "timeout";
    public const string Unknown = "unknown";
}
