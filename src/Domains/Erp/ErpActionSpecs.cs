namespace AgentWorkbench.Erp;

using AgentWorkbench.Ports;

/// <summary>
/// 动作域侧声明的 4 个 ActionSpec 实例（M1 设计 §3.1：类型在底座、实例在动作域）。
/// 全部业务语义集中在本文件与本工程，Core 不认识这些字段。
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

    public static readonly ActionSpec Query = new(QueryKey, QuerySchemaId, false, ActionPrimitives.Query);
    public static readonly ActionSpec Draft = new(DraftKey, DraftSchemaId, false, ActionPrimitives.Draft);
    public static readonly ActionSpec Submit = new(SubmitKey, SubmitSchemaId, true, ActionPrimitives.Submit);
    public static readonly ActionSpec Cancel = new(CancelKey, CancelSchemaId, true, ActionPrimitives.Cancel);

    public static IReadOnlyList<ActionSpec> All => [Query, Draft, Submit, Cancel];

    public static ActionSpec? Find(string actionKey)
        => All.FirstOrDefault(s => string.Equals(s.ActionKey, actionKey, StringComparison.Ordinal));
}

public sealed class ErpActionSpecRegistry : IActionSpecRegistry
{
    public ValueTask<ActionSpec?> FindAsync(string actionKey, CancellationToken ct)
        => ValueTask.FromResult(ErpActionSpecs.Find(actionKey));
}
