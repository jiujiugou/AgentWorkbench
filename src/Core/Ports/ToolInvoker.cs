namespace AgentWorkbench.Ports;

/// <summary>
/// 一次工具调用的结果。刻意只有「成 / 不成 + 原始内容」三项：
/// 解析成业务快照是动作域的事，底座与传输层都不理解返回体的字段含义。
/// </summary>
/// <param name="Ok">调用是否成功。传输失败、工具不存在、工具抛错都归 false。</param>
/// <param name="Content">成功时的原始返回（JSON 文本或工具自己的文本）。</param>
/// <param name="Code">失败归类码，取值见 <see cref="ToolInvocationCodes"/>；成功时为 <c>ok</c>。</param>
/// <param name="Message">失败原因摘要，只用于排障与审计，不进决策。</param>
public sealed record ToolInvocationResult(bool Ok, string? Content, string Code, string? Message)
{
    public static ToolInvocationResult Success(string content)
        => new(true, content, ToolInvocationCodes.Ok, null);

    public static ToolInvocationResult Failure(string code, string message)
        => new(false, null, code, message);
}

/// <summary>工具调用的失败归类。与动作错误码分开：这一层只知道「调用没成」，不知道为什么业务上不允许。</summary>
public static class ToolInvocationCodes
{
    public const string Ok = "ok";
    /// <summary>这个名字不在当前工具目录里（含内容被准入挡掉的情况）。</summary>
    public const string NotFound = "tool_not_found";
    /// <summary>传输层没通（进程没起、握手失败、超时）。</summary>
    public const string Unavailable = "tool_unavailable";
    /// <summary>工具执行时抛错。</summary>
    public const string Failed = "tool_invocation_failed";
}

/// <summary>
/// 按名字调用一个外部工具的能力。<b>这不是给模型用的入口</b>，是给执行体用的出口。
/// </summary>
/// <remarks>
/// 为什么要抽成端口：动作域只允许依赖 <c>Core.Ports</c>（架构门禁）。
/// 有了这个端口，域里写的执行体不必知道对面是 MCP、是 HTTP 还是本地进程，
/// 换传输方式时域代码一行不动 —— 这正是换域 diff 为空那条判据在传输维度的对应物。
/// <para>
/// 约定：<b>实现方不得自行放行</b>。名字找不到就返回 <see cref="ToolInvocationCodes.NotFound"/>，
/// 不要回退到「模糊匹配」或「最近似的一个」——那会把一次明确拒绝变成一次猜测执行。
/// </para>
/// </remarks>
public interface IToolInvoker
{
    /// <summary>这个名字当前是否可调。执行体应在构造参数之外先问一句，好把「没接上」和「调用失败」分开。</summary>
    bool Has(string toolName);

    /// <summary>
    /// 调用。参数是已经拼好的 JSON 对象；返回值原样交回，不做业务解释。
    /// </summary>
    ValueTask<ToolInvocationResult> InvokeAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken ct);
}

/// <summary>
/// 一条「业务动作 → 外部工具」的绑定声明。是数据，不是判定：
/// 它说「`erp.query` 这件事由这个工具完成、参数这样对应」，但<b>不</b>说这样做允不允许 ——
/// 允不允许仍由动作域的权限查询与业务规则回答。
/// </summary>
/// <remarks>
/// 默认值全是「最严」：
/// <list type="bullet">
/// <item><see cref="ArgumentNames"/> 为空 = <b>一个参数都不转发</b>（只有声明过的才出门）；</item>
/// <item><see cref="WritableFieldsArgument"/> 为空 = 该工具没有自由字段参数，字段闸不介入；</item>
/// <item><see cref="WritableFields"/> 为空 = <b>一个字段都不许由请求给</b>（仅当声明了字段参数时生效）。</item>
/// </list>
/// </remarks>
/// <param name="ActionKey">对应哪条 <see cref="ActionSpec"/>；同一 ActionKey 只能有一条绑定。</param>
/// <param name="ToolName">外部工具全名，如 <c>mcp__erpnext__frappe_get_doc</c>。</param>
/// <param name="ArgumentNames">业务参数名 → 工具参数名。<b>未列出的不转发</b>——转发范围就是对外暴露面。</param>
/// <param name="FixedArguments">每次都带的常量参数（如 <c>doctype</c>）。请求里不能覆盖它。</param>
/// <param name="BusinessKeyPath">从返回体里取业务键的路径（点号分隔，如 <c>data.name</c>）。取不到就算不合格。</param>
/// <param name="WritableFieldsArgument">承载「要写哪些字段」的那个对象参数名（如 <c>data</c>）。</param>
/// <param name="WritableFields">允许出现在该对象参数里的字段名；空集 = 一个都不许由请求给。</param>
/// <param name="FixedFieldsJson">每次都写死进去的字段（JSON 对象文本，如 <c>{"docstatus":1}</c>）。请求不能覆盖它。</param>
public sealed record McpActionBinding(
    string ActionKey,
    string ToolName,
    IReadOnlyDictionary<string, string>? ArgumentNames = null,
    IReadOnlyDictionary<string, string>? FixedArguments = null,
    string? BusinessKeyPath = null,
    string? WritableFieldsArgument = null,
    IReadOnlyList<string>? WritableFields = null,
    string? FixedFieldsJson = null)
{
    public static readonly IReadOnlyDictionary<string, string> NoRenames =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Names => ArgumentNames ?? NoRenames;

    public IReadOnlyDictionary<string, string> Constants => FixedArguments ?? NoRenames;

    /// <summary>是否声明了字段闸。没声明不代表放行，而是「这个工具没有可写的自由字段」。</summary>
    public bool GuardsFields => !string.IsNullOrWhiteSpace(WritableFieldsArgument);
}
