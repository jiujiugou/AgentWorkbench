using System.Text.Json;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.Ports;

namespace AgentWorkbench.Tools.Builtin;

/// <summary>
/// 「填单工具」：把模型输出变成一张结构化动作请求，交给既有收单入口。
///
/// 这是底座机制，不是动作域代码：能力清单、描述、参数 Schema 全部来自注入的
/// <see cref="IActionSet.Specs"/> 数据，本类不认识任何一个能力名，也不解析参数含义。
/// 换动作域时本类一行不改，工具描述自动随 <see cref="IActionSpecRegistry.ListAsync"/> 变化。
///
/// 三条边界（破了就等于把执行权交给模型）：
/// 1. 模型输出一律当不可信数据：参数只做「是不是合法 JSON 对象」的格式判断并原样透传，
///    业务含义的校验由动作域注入的 <see cref="ISchemaValidator"/> 负责。
/// 2. 身份只从 <see cref="IActionSubjectProvider"/> 取，工具参数里没有身份字段；
///    取不到身份直接拒绝，不往下递。
/// 3. 只调 <see cref="ActionRunService.SubmitDeferredAsync"/> 这一个既有入口，
///    返回给模型的只有结论码与 requestId，不回业务数据、不回外部单号。
///    四道网关、挂起、幂等、审计的顺序一概不在这里重排。
/// </summary>
public sealed class ProposeActionTool : IAgentToolProvider
{
    public const string ToolName = "propose_action";

    /// <summary>身份取不到时的拒绝码。与网关拒绝码分开，便于区分「没进网关」和「被网关挡了」。</summary>
    public const string SubjectUnavailable = "subject_unavailable";
    public const string InvalidArgumentsJson = "invalid_arguments_json";
    public const string ActionKeyRequired = "action_key_required";

    private readonly IActionSet _actionSet;
    private readonly IActionSubjectProvider _subjects;
    private readonly ActionRunService _runService;
    private readonly IActionOriginProvider? _origin;

    /// <param name="origin">
    /// 调用来源（会话 + 本次输入的客户端消息标识）。缺失时请求号退化为一次性 ID，
    /// 行为与引入去重之前一致；绝不因此放行多个请求共用一个号。
    /// </param>
    public ProposeActionTool(
        IActionSet actionSet,
        IActionSubjectProvider subjects,
        ActionRunService runService,
        IActionOriginProvider? origin = null)
    {
        _actionSet = actionSet;
        _subjects = subjects;
        _runService = runService;
        _origin = origin;
    }

    public IReadOnlyList<AITool> GetTools()
    {
        // 描述是静态文本，构造工具时读一次清单即可；清单是启动时注入的固定实例。
        // ListAsync 在现有实现里都是内存集合，同步取值不会引入 I/O 死锁。
        var specs = _actionSet.Specs.ListAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Func<string, string, Task<string>> propose = ProposeAsync;
        return
        [
            AIFunctionFactory.Create(propose, ToolName, BuildDescription(specs))
        ];
    }

    /// <summary>
    /// 工具本体。做成公开方法是为了能被验收测试直接调用，
    /// 同时 <see cref="GetTools"/> 的委托指向同一个方法，不存在「测试走另一条路」的假绿。
    /// </summary>
    public async Task<string> ProposeAsync(string actionKey, string argumentsJson)
    {
        var subject = _subjects.CurrentSubject;
        if (string.IsNullOrWhiteSpace(subject))
            return Reply("rejected", string.Empty, SubjectUnavailable);

        if (string.IsNullOrWhiteSpace(actionKey))
            return Reply("rejected", string.Empty, ActionKeyRequired);

        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Reply("rejected", string.Empty, InvalidArgumentsJson);
            arguments = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Reply("rejected", string.Empty, InvalidArgumentsJson);
        }

        // requestId 由「哪一次输入 + 哪个能力 + 什么参数」派生，<b>刻意不随机生成</b>：
        // 去重表按 requestId 判重，随机的号让同一件事的每次触发都成为新请求，
        // 「同一 request_id 只出一张单」在模型原样重试面前直接失效。
        // 来源只读宿主作用域（IActionOriginProvider），工具参数里出现同名字段也读不到。
        var requestId = ActionRequestId.Derive(
            _origin?.ConversationId, _origin?.ClientMessageId, actionKey, arguments);
        var request = new ActionRequest(requestId, subject, actionKey, arguments, requestId);
        var result = await _runService.SubmitDeferredAsync(request, CancellationToken.None);

        // 只回结论码。Data / ExternalId 属于业务结果，不进模型上下文。
        var status = result.Status switch
        {
            ActionStatuses.Succeeded => "completed",
            ActionStatuses.Pending => "pending",
            ActionStatuses.Rejected => "rejected",
            _ => "failed",
        };
        return Reply(status, requestId, result.Code);
    }

    private static string BuildDescription(IReadOnlyList<ActionSpec> specs)
    {
        var lines = new List<string>
        {
            "提出一次结构化动作请求。系统随后会独立校验权限与规则，本工具不等于「事情已经办成」。",
            "actionKey 只能从下面的能力清单里选，不要自己发明；argumentsJson 必须是合法 JSON 对象字符串，按该能力的参数 Schema 填。",
            "不要提供 subject / role / approver 等身份或审批字段，身份由系统从当前登录态取，填了也会被忽略。",
            "返回值只有 status、requestId、code，不含业务数据。status=pending 表示等待人工审批，不要重试、不要再次提交；",
            "status=rejected 表示被系统拒绝，按 code 如实告知用户，不要换一个能力名绕过。",
            "能力清单（来自当前动作域）：",
        };

        foreach (var spec in specs)
        {
            lines.Add($"- {spec.ActionKey}：{spec.Description} | 参数 Schema：{spec.InputSchemaJson} | 需人工审批：{spec.RequiresApproval}");
        }

        if (specs.Count == 0)
            lines.Add("（当前动作域没有可用能力，任何请求都会被拒绝。）");

        return string.Join("\n", lines);
    }

    private static string Reply(string status, string requestId, string code)
        => JsonSerializer.Serialize(new { status, requestId, code });
}
