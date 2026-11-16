namespace AgentWorkbench.Erp;

using AgentWorkbench.Ports;

/// <summary>
/// ERP 域的工具策略表：直连的工具调用归到哪个业务动作。
/// </summary>
/// <remarks>
/// 这张表和 <see cref="ErpMcpBindings"/> 是一对反向声明，方向不同、作用也不同：
/// <list type="bullet">
/// <item><b>绑定</b>（闸 → 工具）：我们决定做某件事时，怎么把参数拼成工具调用；</item>
/// <item><b>策略</b>（工具 → 动作）：模型自己发起调用时，怎么认出它等于哪件事。</item>
/// </list>
/// 两条都指向同一个 ActionKey，所以闸、审计、审批只有一份判定。
/// <para>
/// <b>这里只写映射，不写允许。</b> 允不允许由 <c>ErpPermissionPdp</c> 与 <c>ErpBusinessRule</c> 回答。
/// </para>
/// </remarks>
public static class ErpToolPolicies
{
    /// <summary>查单工具：只读，模型可直接调，但仍要过闸（权限、业务规则、审计一次不少）。</summary>
    public const string QueryTool = "mcp__erpnext__frappe_get_doc";

    /// <summary>改单工具：一个工具能做四件事，所以没有默认动作 —— 认不出来就拒。</summary>
    public const string UpdateTool = "mcp__erpnext__frappe_update_doc";

    /// <summary>本域信任的 DocType。模型给的 <c>doctype</c> 必须等于它，否则拒。</summary>
    public const string TrustedDoctype = "Purchase Order";

    public static IReadOnlyList<ToolCallPolicy> All() =>
    [
        new(
            ToolName: QueryTool,
            Mode: ToolCallModes.Gate,
            ActionKey: ErpActionSpecs.QueryKey,
            ArgumentNames: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "document_id",
            },
            RequiredValues: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["doctype"] = TrustedDoctype,
            }),

        new(
            ToolName: UpdateTool,
            Mode: ToolCallModes.Gate,
            // 没有默认动作：这个工具既能改备注也能提交，认不出来就不该放行。
            ActionKey: null,
            Sensitive:
            [
                new SensitiveArgument("data.docstatus", "1", ErpActionSpecs.SubmitKey),
            ],
            ArgumentNames: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "document_id",
            },
            RequiredValues: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["doctype"] = TrustedDoctype,
            }),
    ];
}
