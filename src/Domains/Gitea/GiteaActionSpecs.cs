namespace AgentWorkbench.Gitea;

/// <summary>
/// Gitea 业务能力目录（方案第 3 步）。
///
/// 与 Erp / LabBooking 两个域的关键差别：<b>ActionKey 按业务对象命名，不按原语命名</b>。
/// 原语（Query/Draft/Submit/Cancel）降级成 spec 的一个属性，不再是能力的名字。
/// 模型看到 `issue.create` 就知道能做什么，不必在参数里猜；这是第 4 步「暴露给 Agent」的前提。
///
/// 风险等级只用于展示/排序，决策只看 RequiresApproval 与 PDP/业务规则结论。
/// </summary>
public static class GiteaActionSpecs
{
    public const string RepoQueryKey = "repo.query";
    public const string IssueQueryKey = "issue.query";
    public const string IssueCreateKey = "issue.create";
    public const string IssueCloseKey = "issue.close";

    public const string RepoQuerySchemaId = "gitea.schema.repo.query";
    public const string IssueQuerySchemaId = "gitea.schema.issue.query";
    public const string IssueCreateSchemaId = "gitea.schema.issue.create";
    public const string IssueCloseSchemaId = "gitea.schema.issue.close";

    public static readonly ActionSpec RepoQuery = new(RepoQueryKey, RepoQuerySchemaId, false, ActionPrimitives.Query)
    {
        DisplayName = "查询仓库信息",
        Description = "读取一个 Gitea 仓库的名称、是否私有、当前身份对该仓库的权限（admin/push/pull）与创建时间。只读。参数 owner 为归属用户名，repo 为仓库名；缺省时用连接配置里的默认值。",
        RiskLevel = RiskLevels.Low,
        RequiresApproval = false,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","properties":{"owner":{"type":"string","minLength":1},"repo":{"type":"string","minLength":1}},"additionalProperties":false}
            """,
    };

    public static readonly ActionSpec IssueQuery = new(IssueQueryKey, IssueQuerySchemaId, false, ActionPrimitives.Query)
    {
        DisplayName = "查询议题状态",
        Description = "按议题编号读取 open/closed 状态、标题、创建与关闭时间。只读。也是超时后「只查证、不重放」的目标能力。参数 number 为正整数。",
        RiskLevel = RiskLevels.Low,
        RequiresApproval = false,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["number"],"properties":{"owner":{"type":"string","minLength":1},"repo":{"type":"string","minLength":1},"number":{"type":"integer","minimum":1}},"additionalProperties":false}
            """,
    };

    public static readonly ActionSpec IssueCreate = new(IssueCreateKey, IssueCreateSchemaId, true, ActionPrimitives.Draft)
    {
        DisplayName = "创建议题",
        Description = "在指定仓库创建一条议题，返回外部编号与创建时间。有外部副作用，需要人工审批。注意：原语是 Draft 但 SideEffecting=true —— 原语只描述操作类型，副作用与审批由独立字段决定。标题须以 [AW] 开头。",
        RiskLevel = RiskLevels.Medium,
        RequiresApproval = true,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["title"],"properties":{"owner":{"type":"string","minLength":1},"repo":{"type":"string","minLength":1},"title":{"type":"string","minLength":1,"maxLength":200},"body":{"type":"string"}},"additionalProperties":false}
            """,
    };

    public static readonly ActionSpec IssueClose = new(IssueCloseKey, IssueCloseSchemaId, true, ActionPrimitives.Cancel)
    {
        DisplayName = "关闭议题",
        Description = "把一条议题置为 closed 状态。有外部副作用且状态变化不易回滚，需要人工审批。参数 number 为议题编号。",
        RiskLevel = RiskLevels.High,
        RequiresApproval = true,
        Version = "1",
        InputSchemaJson = """
            {"type":"object","required":["number"],"properties":{"owner":{"type":"string","minLength":1},"repo":{"type":"string","minLength":1},"number":{"type":"integer","minimum":1}},"additionalProperties":false}
            """,
    };

    public static IReadOnlyList<ActionSpec> All => [RepoQuery, IssueQuery, IssueCreate, IssueClose];

    /// <summary>超时查证专用：只读，绝不重放写动作。</summary>
    public static ActionSpec QuerySpec => IssueQuery;

    public static ActionSpec? Find(string actionKey)
        => All.FirstOrDefault(s => string.Equals(s.ActionKey, actionKey, StringComparison.Ordinal));

    public static bool IsWrite(ActionSpec spec)
        => spec.SideEffecting || spec.Primitive is ActionPrimitives.Draft or ActionPrimitives.Submit or ActionPrimitives.Cancel;
}

/// <summary>能力目录注册表：Find 供网关，List 供工具面（第 4 步）。</summary>
public sealed class GiteaActionSpecRegistry : IActionSpecRegistry
{
    public ValueTask<ActionSpec?> FindAsync(string actionKey, CancellationToken ct)
        => ValueTask.FromResult(GiteaActionSpecs.Find(actionKey));

    public ValueTask<IReadOnlyList<ActionSpec>> ListAsync(CancellationToken ct)
        => ValueTask.FromResult(GiteaActionSpecs.All);
}
