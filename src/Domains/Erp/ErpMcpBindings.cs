namespace AgentWorkbench.Erp;

using AgentWorkbench.Ports;

/// <summary>
/// ERP 域的工具绑定：把已有动作键映射到某个外部工具的调用形状。
/// </summary>
/// <remarks>
/// 这份文件是「同一个动作，两种传输」的声明来源：<c>erp.query</c> 的规格、四道闸、审计都不变，
/// 换的是后面的执行体走 HTTP 还是走 MCP。对照跑同一张单，就能看出「换传输不动判定」。
/// <para>
/// 字段全部是声明，不含判定：允不允许仍由 <c>ErpPermissionPdp</c> 与 <c>ErpBusinessRule</c> 回答。
/// </para>
/// </remarks>
public static class ErpMcpBindings
{
    /// <summary>本域期望的工具全名前缀（<c>mcp__{server}__</c>）。实际名字以配置为准。</summary>
    public const string DefaultQueryTool = "mcp__erpnext__frappe_get_doc";

    /// <summary>提交采购单用的工具：Frappe 侧「提交」就是把 <c>docstatus</c> 从 0 改成 1。</summary>
    public const string DefaultSubmitTool = "mcp__erpnext__frappe_update_doc";

    /// <summary>
    /// 只读查询的绑定：业务参数 <c>document_id</c> → 工具参数 <c>name</c>，
    /// 并固定 <c>doctype</c>（请求不能覆盖它，否则一次查询就能指到别的表）。
    /// </summary>
    /// <param name="toolName">工具全名；默认 <see cref="DefaultQueryTool"/>。</param>
    public static McpActionBinding Query(string? toolName = null) => new(
        ActionKey: ErpActionSpecs.QueryKey,
        ToolName: string.IsNullOrWhiteSpace(toolName) ? DefaultQueryTool : toolName,
        ArgumentNames: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["document_id"] = "name",
        },
        FixedArguments: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["doctype"] = "Purchase Order",
        },
        BusinessKeyPath: "data.name");

    /// <summary>
    /// 提交的绑定：只写 <c>docstatus</c>，且值由绑定写死。
    /// </summary>
    /// <remarks>
    /// 这条绑定是「一个动作只能改一个字段」的样板：<c>WritableFields</c> 是空集 ——
    /// 请求<b>不得</b>提供任何字段；<c>FixedFieldsJson</c> 提供唯一的写入内容。
    /// 于是「提交」既不可能顺手改金额，也不可能把 <c>docstatus</c> 改成 0 去反向撤回。
    /// <para>
    /// 反过来，通用的 <c>frappe_update_doc</c> 不带这条绑定的话，一次调用既能改备注也能提交 ——
    /// 那正是准入层把写工具挡在模型目录之外的最后一个原因。
    /// </para>
    /// </remarks>
    public static McpActionBinding Submit(string? toolName = null) => new(
        ActionKey: ErpActionSpecs.SubmitKey,
        ToolName: string.IsNullOrWhiteSpace(toolName) ? DefaultSubmitTool : toolName,
        ArgumentNames: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["document_id"] = "name",
        },
        FixedArguments: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["doctype"] = "Purchase Order",
        },
        BusinessKeyPath: "data.name",
        WritableFieldsArgument: "data",
        WritableFields: [],
        FixedFieldsJson: """{"docstatus":1}""");

    /// <summary>本域全部绑定。新增动作在这里加一条，执行体与闸都不需要改。</summary>
    public static IReadOnlyList<McpActionBinding> All(string? queryToolName = null, string? submitToolName = null)
        => [Query(queryToolName), Submit(submitToolName)];
}
