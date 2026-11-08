namespace AgentWorkbench.Erp;

using AgentWorkbench.Ports;

/// <summary>
/// 动作域侧声明的 4 个 ActionSpec 实例（M1 设计 §3.1：类型在底座、实例在动作域）。
/// 全部业务语义集中在本文件与本工程，Core 不认识这些字段。
///
/// 能力目录字段（DisplayName / Description / RiskLevel / RequiresApproval / Version /
/// InputSchemaJson）由方案第 3 步补齐。注意本域的 ActionKey 仍是 <b>原语粒度</b>
/// （erp.query 而非 po.query），属于 M1 遗留；粒度收敛在 M2 收尾后单独做，
/// 新增的 Gitea 域已按业务对象粒度声明，作为对照样板。
/// </summary>
public static class ErpActionSpecs
{
    public const string QueryKey = "erp.query";
    public const string DraftKey = "erp.draft";
    public const string SubmitKey = "erp.submit";
    public const string CancelKey = "erp.cancel";

    public const string QuerySchemaId = "erp.schema.query";
    public const string DraftSchemaId = "erp.schema.draft";
    public const string SubmitSchemaId = "erp.schema.submit";
    public const string CancelSchemaId = "erp.schema.cancel";

    public static readonly ActionSpec Query = new(QueryKey, QuerySchemaId, false, ActionPrimitives.Query)
    {
        DisplayName = "查询采购订单",
        Description = "按采购单号查询单据当前状态、金额、供应商与最后变更时间。只读取，不修改任何数据。用于回答「这张单现在怎么样」。参数 document_id 形如 PO-2026-1108。",
        RiskLevel = RiskLevels.Low,
        RequiresApproval = false,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["document_id"],"properties":{"document_id":{"type":"string","minLength":1,"description":"采购单号"}},"additionalProperties":false}
            """,
    };

    public static readonly ActionSpec Draft = new(DraftKey, DraftSchemaId, false, ActionPrimitives.Draft)
    {
        DisplayName = "登记采购草稿",
        Description = "在系统内登记一张采购草稿，不向外部系统提交。用于先把意图落成可核对的记录。参数 document_id 为拟用单号，amount 为含税金额（元）。",
        RiskLevel = RiskLevels.Medium,
        RequiresApproval = false,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["document_id","amount"],"properties":{"document_id":{"type":"string","minLength":1},"amount":{"type":"number","exclusiveMinimum":0}},"additionalProperties":false}
            """,
    };

    public static readonly ActionSpec Submit = new(SubmitKey, SubmitSchemaId, true, ActionPrimitives.Submit)
    {
        DisplayName = "提交采购订单",
        Description = "把采购单正式提交到外部系统，产生真实外部副作用。需要人工审批；金额超过 5 万档位需更高权限。同一 request_id 只允许产生一张单，超时只查证不重放。",
        RiskLevel = RiskLevels.High,
        RequiresApproval = true,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["document_id","amount"],"properties":{"document_id":{"type":"string","minLength":1},"amount":{"type":"number","exclusiveMinimum":0}},"additionalProperties":false}
            """,
    };

    public static readonly ActionSpec Cancel = new(CancelKey, CancelSchemaId, true, ActionPrimitives.Cancel)
    {
        DisplayName = "取消采购订单",
        Description = "撤销一张已登记的采购单。有外部副作用且难以回滚（可能需要红冲），必须人工审批。已取消的单据不可重复取消。",
        RiskLevel = RiskLevels.Critical,
        RequiresApproval = true,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["document_id"],"properties":{"document_id":{"type":"string","minLength":1},"reason":{"type":"string"}},"additionalProperties":false}
            """,
    };

    public static IReadOnlyList<ActionSpec> All => [Query, Draft, Submit, Cancel];

    public static ActionSpec? Find(string actionKey)
        => All.FirstOrDefault(s => string.Equals(s.ActionKey, actionKey, StringComparison.Ordinal));
}

public sealed class ErpActionSpecRegistry : IActionSpecRegistry
{
    public ValueTask<ActionSpec?> FindAsync(string actionKey, CancellationToken ct)
        => ValueTask.FromResult(ErpActionSpecs.Find(actionKey));

    public ValueTask<IReadOnlyList<ActionSpec>> ListAsync(CancellationToken ct)
        => ValueTask.FromResult(ErpActionSpecs.All);
}
