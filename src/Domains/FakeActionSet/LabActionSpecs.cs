namespace AgentWorkbench.LabBooking;

using AgentWorkbench.Ports;

/// <summary>
/// 第二动作域侧声明的 4 个 ActionSpec 实例（M1 设计 §3.1 / §5.3）。
/// 全部业务语义集中在本工程，Core 不认识这些字段。
/// QuerySpec 指向 lab.query，用于超时后的「只查证、不重放」。
///
/// 能力目录字段按方案第 3 步补齐；字段、判据与 Erp 域完全不同，
/// 是 AC-01「换域后底座 diff 为空」的对照载体。
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

    public static readonly ActionSpec Query = new(QueryKey, QuerySchemaId, false, ActionPrimitives.Query)
    {
        DisplayName = "查询实验室预约",
        Description = "按预约单号查询实验室预约的时段、参与人数与审批状态。只读，不修改数据。参数 lab_id 为实验室编号。",
        RiskLevel = RiskLevels.Low,
        RequiresApproval = false,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["lab_id"],"properties":{"lab_id":{"type":"string","minLength":1}},"additionalProperties":false}
            """,
    };

    public static readonly ActionSpec Draft = new(DraftKey, DraftSchemaId, false, ActionPrimitives.Draft)
    {
        DisplayName = "登记实验室预约草稿",
        Description = "登记一条实验室使用申请草稿，不产生外部预约记录。参数 lab_id 为实验室编号，slot_hours 为使用时长，attendees 为参与人数。",
        RiskLevel = RiskLevels.Medium,
        RequiresApproval = false,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["lab_id","slot_hours","attendees"],"properties":{"lab_id":{"type":"string","minLength":1},"slot_hours":{"type":"number","exclusiveMinimum":0},"attendees":{"type":"integer","minimum":1}},"additionalProperties":false}
            """,
    };

    public static readonly ActionSpec Submit = new(SubmitKey, SubmitSchemaId, true, ActionPrimitives.Submit)
    {
        DisplayName = "确认实验室预约",
        Description = "正式确认实验室预约，占用时段并生成 LB- 单号。需要实验室管理员审批；使用时长超过 4 小时或人数超过 12 人不予通过。",
        RiskLevel = RiskLevels.High,
        RequiresApproval = true,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["lab_id","slot_hours","attendees"],"properties":{"lab_id":{"type":"string","minLength":1},"slot_hours":{"type":"number","exclusiveMinimum":0},"attendees":{"type":"integer","minimum":1}},"additionalProperties":false}
            """,
    };

    public static readonly ActionSpec Cancel = new(CancelKey, CancelSchemaId, true, ActionPrimitives.Cancel)
    {
        DisplayName = "取消实验室预约",
        Description = "取消一条已确认的实验室预约并释放时段。有外部副作用，需要人工审批；仅未取消的预约可取消。",
        RiskLevel = RiskLevels.Critical,
        RequiresApproval = true,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["lab_id"],"properties":{"lab_id":{"type":"string","minLength":1},"reason":{"type":"string"}},"additionalProperties":false}
            """,
    };

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

    public ValueTask<IReadOnlyList<ActionSpec>> ListAsync(CancellationToken ct)
        => ValueTask.FromResult(LabActionSpecs.All);
}
