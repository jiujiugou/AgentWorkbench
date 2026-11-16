namespace AgentWorkbench.Webapi.Actions;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.Ports;

/// <summary>
/// 直连工具的动作闸：把模型发起的工具调用归一成一次业务动作，交给<b>唯一那份</b>四道闸。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：模型现在可以直接调 MCP 工具（自己拼参数），而四道闸原来只挂在
/// <c>propose_action</c> 后面。若不接这一层，直连就是一条绕过闸的路；若在这里复制一套闸，
/// 就会出现两个「谁批准的」答案。所以这里只做一件事——<b>翻译身份</b>，判定全部下沉到
/// <see cref="ActionRunService"/>。
/// </para>
/// <para>
/// 三条默认都是拒：
/// <list type="number">
/// <item>没有策略条目 → 不归本闸管（也就不该出现在模型目录里）；</item>
/// <item>有条目但认不出是哪个动作 → 拒（<c>unrecognized_call</c>）；</item>
/// <item>认出来了但取不到当前身份 → 拒（<c>subject_missing</c>）。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ActionToolGateway : IToolCallGateway
{
    private readonly ActionRunService _runs;
    private readonly IActionSubjectProvider _subjects;
    private readonly IReadOnlyList<ToolCallPolicy> _policies;

    public ActionToolGateway(
        ActionRunService runs,
        IActionSubjectProvider subjects,
        IReadOnlyList<ToolCallPolicy> policies)
    {
        _runs = runs;
        _subjects = subjects;
        _policies = policies;
    }

    public bool Handles(string toolName)
        => Find(toolName) is not null;

    public async ValueTask<ToolCallOutcome> ExecuteAsync(
        string toolName,
        string argumentsJson,
        CancellationToken ct)
    {
        var policy = Find(toolName);
        if (policy is null)
            return Deny("tool_not_governed", $"工具没有策略条目：{toolName}");

        // 身份只从宿主认证态取。模型给的参数里就算写了 user，也不看。
        var subject = _subjects.CurrentSubject;
        if (string.IsNullOrWhiteSpace(subject))
            return Deny("subject_missing", "无法确认当前主体，按拒绝处理");

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Deny("invalid_arguments", "参数不是合法 JSON");
        }

        if (arguments.ValueKind != JsonValueKind.Object)
            return Deny("invalid_arguments", "参数必须是 JSON 对象");

        // 常量参数：模型不能靠换一个 doctype 把调用指到别的表。
        foreach (var required in policy.RequiredValues ?? new Dictionary<string, string>())
        {
            if (!arguments.TryGetProperty(required.Key, out var actual)
                || actual.ValueKind != JsonValueKind.String
                || !string.Equals(actual.GetString(), required.Value, StringComparison.Ordinal))
                return Deny("argument_denied", $"参数 {required.Key} 必须等于 {required.Value}");
        }

        var actionKey = Classify(arguments, policy);
        if (actionKey is null)
            return Deny("unrecognized_call",
                $"认不出这次调用等于哪个动作：{toolName}（未命中任何已登记的敏感参数）");

        var request = new ActionRequest(
            RequestId: Guid.NewGuid().ToString("N"),
            Subject: subject,
            ActionKey: actionKey,
            Arguments: MapArguments(arguments, policy),
            IdempotencyKey: Fingerprint(toolName, argumentsJson));

        var result = await _runs.RunAsync(request, ct);
        return new ToolCallOutcome(
            Ok: string.Equals(result.Status, ActionStatuses.Succeeded, StringComparison.Ordinal),
            Code: result.Code,
            Payload: ToPayload(actionKey, result));
    }

    private ToolCallPolicy? Find(string toolName)
        => _policies.FirstOrDefault(policy =>
            policy.IsGate && string.Equals(policy.ToolName, toolName, StringComparison.Ordinal));

    /// <summary>
    /// 认出这次调用等于哪个动作：先看敏感参数，再看默认动作，都没有就是<b>认不出来</b>。
    /// </summary>
    /// <remarks>
    /// 比较用「原始文本」而不是「字符串类型」：<c>docstatus</c> 在 Frappe 里是数字，
    /// 只认字符串会让 <c>{"docstatus":1}</c> 这种最常见的写法漏判 —— 而漏判等于放行。
    /// </remarks>
    private static string? Classify(JsonElement arguments, ToolCallPolicy policy)
    {
        foreach (var sensitive in policy.Sensitive ?? [])
        {
            if (!TryReadPath(arguments, sensitive.Path, out var value))
                continue;

            var actual = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();

            if (string.Equals(actual, sensitive.Value, StringComparison.Ordinal))
                return sensitive.ActionKey;
        }

        return policy.ActionKey;
    }

    /// <summary>
    /// 工具参数 → 业务参数。<b>只转发策略里声明过的</b>：
    /// 转发范围就是动作能看到的输入面，多一个字段都可能改变判定。
    /// </summary>
    private static JsonElement MapArguments(JsonElement arguments, ToolCallPolicy policy)
    {
        var mapped = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (from, to) in policy.ArgumentNames ?? new Dictionary<string, string>())
        {
            if (arguments.TryGetProperty(from, out var value))
                mapped[to] = value.Clone();
        }

        return JsonSerializer.SerializeToElement(mapped);
    }

    /// <summary>点号路径取值，如 <c>data.docstatus</c>。取不到返回 false，不猜。</summary>
    private static bool TryReadPath(JsonElement root, string path, out JsonElement value)
    {
        value = default;
        var node = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
                return false;
        }

        value = node;
        return true;
    }

    /// <summary>
    /// 调用指纹：工具名 + 原始参数文本。直连拿不到业务请求号，幂等只能到这一级 ——
    /// 原样重发能命中，换个措辞就命中不了，这是这套形状的已知上界。
    /// </summary>
    private static string Fingerprint(string toolName, string argumentsJson)
    {
        var bytes = Encoding.UTF8.GetBytes(toolName + "\n" + argumentsJson);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// 回填给模型的载荷。刻意带上动作身份：模型要知道「你这次被当成提交处理了」，
    /// 否则它只看到一串单据字段，会以为那只是一次普通更新。
    /// </summary>
    private static string ToPayload(string actionKey, ActionResult result)
        => JsonSerializer.Serialize(new
        {
            action = actionKey,
            status = result.Status,
            code = result.Code,
            external_id = result.ExternalId,
            data = result.Data,
        });

    private static ToolCallOutcome Deny(string code, string reason)
        => new(Ok: false, Code: code,
            Payload: JsonSerializer.Serialize(new { denied = true, reason }));
}
