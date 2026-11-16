namespace AgentWorkbench.Ports;

/// <summary>工具调用的处置方式。</summary>
public static class ToolCallModes
{
    /// <summary>过闸：归一成业务动作，由 <c>ActionRunService</c> 走四道闸。</summary>
    public const string Gate = "gate";

    /// <summary>豁免：点名放行的旁路（只读检索一类）。<b>必须显式登记</b>，不能靠没写来默认。</summary>
    public const string Passthrough = "passthrough";
}

/// <summary>
/// 一条「参数出现某个值 ⇒ 这次调用等于某个业务动作」的映射。
/// </summary>
/// <remarks>
/// 这是<b>数据</b>，不是判定：它只说「`data.docstatus=1` 这件事的名字叫 `erp.submit`」，
/// 不说这么做允不允许 —— 允不允许仍由权限查询与业务规则回答。
/// <para>
/// 为什么必须有它：直连时模型自己拼参数，<c>frappe_update_doc</c> 一次调用可以是改备注、
/// 提交或取消。不读参数就分不开这三件事，而审批与审计必须知道「这次是哪件事」才有对象。
/// </para>
/// </remarks>
/// <param name="Path">参数里的点号路径，如 <c>data.docstatus</c>。</param>
/// <param name="Value">该路径等于这个值（按字符串比较）时命中。</param>
/// <param name="ActionKey">命中的动作键。</param>
public sealed record SensitiveArgument(string Path, string Value, string ActionKey);

/// <summary>
/// 一个外部工具在我们这边的处置策略。
/// </summary>
/// <remarks>
/// <b>没有条目 = 不存在。</b> 这条默认值是整套设计的支点：
/// MCP server 随时可能新增工具，若「没写策略」等于「默认放行」，一次 server 升级
/// 就会变成一次权限变化，而且没人会注意到。
/// </remarks>
/// <param name="ToolName">工具全名（<c>mcp__{server}__{tool}</c>）。</param>
/// <param name="Mode"><see cref="ToolCallModes"/> 之一。</param>
/// <param name="ActionKey">未命中敏感参数时的默认动作；<c>null</c> 表示「认不出来就拒」。</param>
/// <param name="Sensitive">参数值 → 动作 的映射，先于 <paramref name="ActionKey"/> 判定。</param>
/// <param name="ArgumentNames">工具参数名 → 业务参数名的反向映射（直连方向：模型给工具名，我们取业务名）。</param>
/// <param name="RequiredValues">必须相等的常量参数（如 <c>doctype</c>），不等即拒。</param>
public sealed record ToolCallPolicy(
    string ToolName,
    string Mode,
    string? ActionKey = null,
    IReadOnlyList<SensitiveArgument>? Sensitive = null,
    IReadOnlyDictionary<string, string>? ArgumentNames = null,
    IReadOnlyDictionary<string, string>? RequiredValues = null)
{
    public bool IsGate => string.Equals(Mode, ToolCallModes.Gate, StringComparison.Ordinal);
}

/// <summary>
/// 直连工具调用的闸门：把一个模型发起的工具调用，变成一次由四道闸裁决的动作。
/// </summary>
/// <remarks>
/// 位置在 <c>ToolExecutionPipeline.FunctionInvoker</c> 里，即「模型已生成调用、尚未执行」的那一刻。
/// 实现方必须是<b>唯一的判定点</b> —— 不允许再复制一套闸，否则会同时存在两个「谁批准的」答案。
/// </remarks>
public interface IToolCallGateway
{
    /// <summary>这个名字是否归本闸管。返回 <c>false</c> 时调用方按原路径执行工具。</summary>
    bool Handles(string toolName);

    /// <summary>
    /// 执行一次调用。<paramref name="argumentsJson"/> 是模型给的原始参数（JSON 对象文本）。
    /// </summary>
    ValueTask<ToolCallOutcome> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct);
}

/// <summary>一次调用被裁决后的结果。<see cref="Payload"/> 原样回填给模型。</summary>
/// <param name="Ok">是否成功（含「被拒」也算不成功——模型据此改路或收口）。</param>
/// <param name="Code">归类码：动作终态码或拒绝码。</param>
/// <param name="Payload">回填给模型的文本。</param>
public sealed record ToolCallOutcome(bool Ok, string Code, string Payload);
