namespace AgentWorkbench.LabBooking;

using AgentWorkbench.Ports;

/// <summary>
/// 第二动作域侧声明的 4 个 ActionSpec 实例（M1 设计 §3.1 / §5.3）。
/// 全部业务语义集中在本工程，Core 不认识这些字段。
/// QuerySpec 指向 lab.query，用于超时后的「只查证、不重放」。
/// </summary>
public static class LabActionSpecs
{
    public const string QueryKey = "lab.query";
    public const string DraftKey = "lab.draft";
    public const string SubmitKey = "lab.submit";
    public const string CancelKey = "lab.cancel";

    public const string QuerySchemaId = "lab.schema.query";
    public const string DraftSchemaId = "lab.schema.draft";
    public const string SubmitSchemaId = "lab.schema.submit";
    public const string CancelSchemaId = "lab.schema.cancel";

    public static readonly ActionSpec Query = new(QueryKey, QuerySchemaId, false, ActionPrimitives.Query);
    public static readonly ActionSpec Draft = new(DraftKey, DraftSchemaId, false, ActionPrimitives.Draft);
    public static readonly ActionSpec Submit = new(SubmitKey, SubmitSchemaId, true, ActionPrimitives.Submit);
    public static readonly ActionSpec Cancel = new(CancelKey, CancelSchemaId, true, ActionPrimitives.Cancel);

    // 超时查证专用规格，等价于上面 lab.query 的实例。
    public static ActionSpec QuerySpec => Query;

    public static IReadOnlyList<ActionSpec> All => [Query, Draft, Submit, Cancel];

    public static ActionSpec? Find(string actionKey)
        => All.FirstOrDefault(s => string.Equals(s.ActionKey, actionKey, StringComparison.Ordinal));
}

/// <summary>动作类型注册表：底座按 ActionKey 取规格，实例由本动作域注入。</summary>
public sealed class LabActionSpecRegistry : IActionSpecRegistry
{
    public ValueTask<ActionSpec?> FindAsync(string actionKey, CancellationToken ct)
        => ValueTask.FromResult(LabActionSpecs.Find(actionKey));
}
