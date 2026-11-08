namespace AgentWorkbench.Gitea;

/// <summary>
/// 第 1 道：动作域参数校验。按 ActionKey 判断，底座不理解任何字段含义。
/// InputSchemaJson 是给模型/工具面看的声明，这里是服务端与之对应的确定性校验。
/// </summary>
public sealed class GiteaSchemaValidator : ISchemaValidator
{
    public ValueTask<SchemaDecision> ValidateAsync(ActionSpec spec, ActionRequest request, CancellationToken ct)
    {
        var args = request.Arguments;
        if (args.ValueKind != JsonValueKind.Object)
            return ValueTask.FromResult(new SchemaDecision(false, "arguments must be a JSON object"));

        if (args.TryGetProperty("owner", out var owner) &&
            (owner.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(owner.GetString())))
            return ValueTask.FromResult(new SchemaDecision(false, "owner must be a non-empty string"));

        if (args.TryGetProperty("repo", out var repo) &&
            (repo.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(repo.GetString())))
            return ValueTask.FromResult(new SchemaDecision(false, "repo must be a non-empty string"));

        if (spec.ActionKey is GiteaActionSpecs.IssueQueryKey or GiteaActionSpecs.IssueCloseKey)
        {
            if (!GiteaArguments.TryReadNumber(args, out var number) || number <= 0)
                return ValueTask.FromResult(new SchemaDecision(false, "number must be a positive integer"));
        }

        if (spec.ActionKey == GiteaActionSpecs.IssueCreateKey)
        {
            if (!args.TryGetProperty("title", out var title) ||
                title.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(title.GetString()))
                return ValueTask.FromResult(new SchemaDecision(false, "title is required"));
            if (title.GetString()!.Length > 200)
                return ValueTask.FromResult(new SchemaDecision(false, "title exceeds 200 chars"));
        }

        return ValueTask.FromResult(new SchemaDecision(true, $"schema {spec.SchemaId} ok"));
    }
}

/// <summary>
/// 第 2 道：唯一 PDP。真实 Gitea 侧还有一层 token scope，本 PDP 是我们侧的前置判定。
/// 两侧都拒绝才安全：本 PDP 挡在发请求之前（零调用），对方 scope 拦截是最后兜底。
/// </summary>
public sealed class GiteaPermissionPdp : IPermissionQuery
{
    public bool DenyAll { get; set; }

    /// <summary>写能力额外要求的主体集合；读能力只要求属于 AllowedSubjects。</summary>
    public ISet<string> AllowedSubjects { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "wang" };

    public ISet<string> AllowedWriters { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "wang" };

    public ValueTask<PermissionDecision> QueryAsync(PermissionQuery query, CancellationToken ct)
    {
        if (DenyAll || !AllowedSubjects.Contains(query.Subject))
            return ValueTask.FromResult(new PermissionDecision(false, "subject has no gitea capability"));

        var isWrite = query.ActionKey is GiteaActionSpecs.IssueCreateKey or GiteaActionSpecs.IssueCloseKey;
        if (isWrite && !AllowedWriters.Contains(query.Subject))
            return ValueTask.FromResult(new PermissionDecision(false, "subject has no write capability"));

        return ValueTask.FromResult(new PermissionDecision(true, "subject holds gitea capability"));
    }
}

/// <summary>
/// 第 3 道：业务判据。Gitea 没有「金额档位」，改用「标题必须带 [AW] 前缀」。
/// 这正好印证：第四维永远是动作域自己的语义，做不成底座通用件。
/// </summary>
public sealed class GiteaBusinessRule : IBusinessRule
{
    public const string TitlePrefix = "[AW]";

    public ValueTask<RuleDecision> EvaluateAsync(ActionRequest request, CancellationToken ct)
    {
        IReadOnlyList<EvidenceRef> evidence =
        [
            new EvidenceRef("gitea-policy-1", "外部系统写入约定.md", 1, 1),
        ];

        if (request.ActionKey != GiteaActionSpecs.IssueCreateKey)
            return ValueTask.FromResult(new RuleDecision(true, "只读动作无需业务判据", evidence));

        var args = request.Arguments;
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("title", out var title) ||
            title.ValueKind != JsonValueKind.String ||
            !title.GetString()!.StartsWith(TitlePrefix, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new RuleDecision(
                false, $"写入议题的标题必须以 {TitlePrefix} 开头，便于事后识别来源", evidence));
        }

        return ValueTask.FromResult(new RuleDecision(true, "标题前缀符合写入约定", evidence));
    }
}

/// <summary>审批闸：审批人由动作域注入，底座只拿 Approved 与 Approver。</summary>
public sealed class GiteaApprovalGate : IApprovalGate
{
    public bool Approved { get; set; } = true;
    public string ApproverName { get; set; } = "仓库管理员";

    public ValueTask<ApprovalDecision> RequestAsync(ActionRequest request, CancellationToken ct)
        => ValueTask.FromResult(new ApprovalDecision(
            Approved,
            Approved ? ApproverName : null,
            Approved ? "人工审批通过" : "人工审批拒绝"));
}

/// <summary>动作域内部的参数读取 helper。底座不做这件事。</summary>
internal static class GiteaArguments
{
    public static bool TryReadNumber(JsonElement args, out int number)
    {
        number = 0;
        return args.ValueKind == JsonValueKind.Object
               && args.TryGetProperty("number", out var node)
               && node.ValueKind == JsonValueKind.Number
               && node.TryGetInt32(out number);
    }

    public static string ReadString(JsonElement args, string name, string fallback)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var node)
           && node.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(node.GetString())
            ? node.GetString()!
            : fallback;

    public static string EscapePath(string value)
        => Uri.EscapeDataString(value);
}
