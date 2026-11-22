namespace AgentWorkbench.Erp;

using AgentWorkbench.Ports;

/// <summary>
/// ERP 域的工具绑定：把已有动作键映射到某个外部工具的调用形状。
/// </summary>
/// <remarks>
/// 这份文件是四个动作的执行翻译表：<c>erp.query</c> / <c>erp.draft</c> / <c>erp.submit</c> / <c>erp.cancel</c>
/// 的规格、四道闸、审计都不变，这里只说「到了执行体，调哪个工具、参数怎么对应」。
/// <para>
/// 字段全部是声明，不含判定：允不允许仍由 <c>ErpPermissionPdp</c> 与 <c>ErpBusinessRule</c> 回答。
/// 写死常量（<c>doctype</c> / <c>docstatus</c>）由绑定持有，请求不能覆盖 —— 所以「提交」不可能顺手改金额，
/// 「取消」也不可能反向撤回；通用写工具不会直接暴露给模型。
/// </para>
/// </remarks>
public static class ErpMcpBindings
{
    /// <summary>本域期望的工具全名前缀（<c>mcp__{server}__</c>）。实际名字以配置为准。</summary>
    public const string DefaultQueryTool = "mcp__erpnext__frappe_get_doc";

    /// <summary>登记采购草稿用的工具：Frappe 侧「插入单据」得到一张未提交的草稿。</summary>
    public const string DefaultDraftTool = "mcp__erpnext__frappe_create_doc";

    /// <summary>提交采购单用的工具：Frappe 侧「提交」就是把 <c>docstatus</c> 从 0 改成 1。</summary>
    public const string DefaultSubmitTool = "mcp__erpnext__frappe_update_doc";

    /// <summary>取消采购单用的工具：与提交同一工具，区别只在绑定写死的 <c>docstatus</c> 值。</summary>
    public const string DefaultCancelTool = "mcp__erpnext__frappe_update_doc";

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
    /// 草稿的绑定：调「创建单据」而不是「更新单据」。
    /// </summary>
    /// <remarks>
    /// 为什么 <c>document_id</c> 不映射成 <c>name</c>：<c>frappe_create_doc</c> 的 name 由对方命名序列分配，
    /// 传入的 name 会被忽略。所以「拟用单号」只是请求侧的判定字段（供幂等与审计引用），不能冒充对方主键；
    /// 真正的业务键从返回体 <c>data.name</c> 取回 —— 这正是「对方说了算」而不是我们指定主键。
    /// <para>
    /// <c>amount</c> 通过 <see cref="McpActionBinding.DataFields"/> 落进 <c>data.remarks</c>：它不是顶层工具参数，
    /// 只能出现在 data 对象里。这里选备注作为落点而非 Frappe 的只读金额字段（<c>grand_total</c> 由明细算出），
    /// 保证「写进去的值真的留下」。一张完整采购单还需要 supplier / items 等必填字段 —— 那是业务语义的重量，
    /// 不属于「传输绑定」这一层；真跑时若被 Frappe 拒绝，会明确失败，不会假装成功。
    /// </para>
    /// </remarks>
    public static McpActionBinding Draft(string? toolName = null) => new(
        ActionKey: ErpActionSpecs.DraftKey,
        ToolName: string.IsNullOrWhiteSpace(toolName) ? DefaultDraftTool : toolName,
        FixedArguments: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["doctype"] = "Purchase Order",
        },
        BusinessKeyPath: "data.name",
        WritableFieldsArgument: "data",
        WritableFields: ["remarks"],
        DataFields: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount"] = "remarks",
        });

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

    /// <summary>
    /// 取消的绑定：与提交同一工具、同一参数形状，只有写死的 <c>docstatus</c> 不同（2 = 已取消）。
    /// </summary>
    /// <remarks>
    /// 和提交严格对称：请求只提供 <c>document_id</c> → <c>name</c>，<c>WritableFields</c> 为空集，
    /// 写入内容只有 <c>{"docstatus":2}</c>。于是「取消」既不能顺手改金额，也不能被请求塞回 1 变相提交。
    /// <c>reason</c> 不进写入对象 —— 它属于我们的审计留痕，不属于对方单据字段。
    /// </remarks>
    public static McpActionBinding Cancel(string? toolName = null) => new(
        ActionKey: ErpActionSpecs.CancelKey,
        ToolName: string.IsNullOrWhiteSpace(toolName) ? DefaultCancelTool : toolName,
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
        FixedFieldsJson: """{"docstatus":2}""");

    /// <summary>本域全部绑定。新增动作在这里加一条，执行体与闸都不需要改。</summary>
    public static IReadOnlyList<McpActionBinding> All(
        string? queryToolName = null,
        string? draftToolName = null,
        string? submitToolName = null,
        string? cancelToolName = null)
        => [Query(queryToolName), Draft(draftToolName), Submit(submitToolName), Cancel(cancelToolName)];
}
