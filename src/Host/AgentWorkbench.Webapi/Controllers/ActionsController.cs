namespace AgentWorkbench.Webapi.Controllers;

using System.Text.Json;
using AgentWorkbench.ActionGateway;
using AgentWorkbench.Ports;
using AgentWorkbench.Webapi.Actions;
using AgentWorkbench.Webapi.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PortsActionResult = AgentWorkbench.Ports.ActionResult;

/// <summary>
/// M1 受控动作的最小宿主闭环（M1 设计 §2.2 步骤 1 / 步骤 4）：
/// 提交 → 四道校验 → 挂起等人工 → 同意/拒绝 → 执行 → 终态 + 审计反查。
///
/// 归属只信 Claim：Subject 取当前登录用户，客户端传不了别人的身份（同 CurrentUser 的约定）。
/// 不做前端页面之外的东西：无管理后台、无通用连接器、无第二个真实动作域。
/// </summary>
[ApiController]
[Route("api/actions")]
public sealed class ActionsController : ControllerBase
{
    private readonly ActionRunService _service;
    private readonly IActionRunStore _store;
    private readonly IActionSubjectProvider _subjects;

    public ActionsController(ActionRunService service, IActionRunStore store, IActionSubjectProvider subjects)
    {
        _service = service;
        _store = store;
        _subjects = subjects;
    }

    /// <summary>提交一次结构化动作请求。副作用动作返回 202 + Pending，等审批。</summary>
    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] ActionSubmitBody body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body.ActionKey))
            return BadRequest(new { code = "action_key_required" });

        // 输入身份必填。以前允许客户端自报 requestId、为空时服务端随机生成，
        // 那等于把判重权交给调用方：换个号重放就成了另一件事。现在由 Host 统一算号。
        if (!InputIdentity.IsPresent(body.ClientMessageId))
            return BadRequest(new { code = InputIdentity.MissingCode });

        // 归属声明必须齐全（中间件已挡一次，这里双保险）。
        var (_, userId) = User.Owner();
        // Subject 走统一身份端口：HTTP 与 Hub 各入口不再各写一遍。
        // 仍用登录名而不是内部 GUID：动作域的权限判据按人/工号走（PDP 白名单是 "wang" 这类名字），
        // GUID 只承担会话与档案的归属隔离。两者混用会让真实请求在第 2 道被判 permission_denied。
        var subject = _subjects.CurrentSubject ?? userId;
        var argumentsJson = body.Arguments is { } args && args.ValueKind != JsonValueKind.Undefined
            ? args.GetRawText()
            : "{}";

        using var document = JsonDocument.Parse(argumentsJson);
        var arguments = document.RootElement.Clone();

        // 无会话入口：用归属 id 当隔离维度（与工具环的 conversationId 同一位置）。
        // 规则仍是 Core 那套 ActionRequestId.Derive，Host 只补「必须先有输入身份」这一格。
        var requestId = InputIdentity.Derive(userId, body.ClientMessageId, body.ActionKey, arguments);
        var request = new ActionRequest(requestId, subject, body.ActionKey, arguments, requestId);

        var result = await _service.SubmitDeferredAsync(request, cancellationToken);

        return result.Status switch
        {
            ActionStatuses.Pending => Accepted(ToDto(result)),
            ActionStatuses.Rejected => UnprocessableEntity(ToDto(result)),
            _ => Ok(ToDto(result)),
        };
    }

    /// <summary>按 request_id 查终态；仍在待审时返回 202，不存在返回 404。</summary>
    [HttpGet("{requestId}")]
    public async Task<IActionResult> Get(string requestId, CancellationToken cancellationToken)
    {
        var result = await _store.LoadResultAsync(requestId, cancellationToken);
        if (result is not null)
            return Ok(ToDto(result));

        var pending = await _service.LoadPendingRequestAsync(requestId, cancellationToken);
        if (pending is not null)
            return Accepted(new { requestId, status = ActionStatuses.Pending, code = ActionRejectionCodes.ApprovalRequired });

        return NotFound(new { code = "not_found", requestId });
    }

    /// <summary>审计反查：谁 / 何时 / 看到什么 / 依据哪条规则 / 变成什么。</summary>
    [HttpGet("{requestId}/audit")]
    public async Task<IActionResult> Audit(string requestId, CancellationToken cancellationToken)
        => Ok(await _service.AuditTrailAsync(requestId, cancellationToken));

    /// <summary>待办列表：只给已挂起的请求快照，不含业务单据状态。</summary>
    [HttpGet("pending")]
    public async Task<IActionResult> Pending(CancellationToken cancellationToken)
    {
        var checkpoints = await _store.ListCheckpointsAsync(ActionRunStates.PendingApproval, cancellationToken);
        var rows = new List<object>();
        foreach (var checkpoint in checkpoints)
        {
            var request = await _service.LoadPendingRequestAsync(checkpoint.RequestId, cancellationToken);
            if (request is null)
                continue;

            rows.Add(new
            {
                requestId = checkpoint.RequestId,
                actionKey = request.ActionKey,
                subject = request.Subject,
                arguments = request.Arguments,
                at = checkpoint.At,
                actionSet = _service.ActionSetName,
            });
        }

        return Ok(rows);
    }

    /// <summary>人工同意。审批人取当前登录用户的显示名，不信请求体；仅 owner 角色可批。</summary>
    [HttpPost("{requestId}/approve")]
    [Authorize(Roles = "owner")]
    public async Task<IActionResult> Approve(string requestId, CancellationToken cancellationToken)
    {
        var approver = User.DisplayName() ?? User.UserId() ?? "unknown";
        var result = await _service.ApproveAsync(requestId, approver, cancellationToken);
        return result is null
            ? NotFound(new { code = "not_pending", requestId })
            : Ok(ToDto(result));
    }

    /// <summary>人工拒绝：零外部调用，直接进终态；仅 owner 角色可拒。</summary>
    [HttpPost("{requestId}/reject")]
    [Authorize(Roles = "owner")]
    public async Task<IActionResult> Reject(
        string requestId,
        [FromBody] ActionRejectBody? body,
        CancellationToken cancellationToken)
    {
        var result = await _service.RejectAsync(
            requestId,
            string.IsNullOrWhiteSpace(body?.Reason) ? "rejected by human" : body!.Reason!,
            cancellationToken);
        return result is null
            ? NotFound(new { code = "not_pending", requestId })
            : Ok(ToDto(result));
    }

    private static ActionResultDto ToDto(PortsActionResult result)
        => new(result.RequestId, result.Status, result.Code, result.ExternalId, result.ErrorCode, result.Data);
}

/// <summary>
/// 提交体。Arguments 对底座不透明，这里只做透传。
/// <c>ClientMessageId</c> = 「这是哪一次输入」，由调用方在发起时生成、重放时复用；
/// 请求号由服务端派生，<b>不接受客户端自报 requestId</b>（那会把判重权交出去）。
/// </summary>
public sealed record ActionSubmitBody(
    string ActionKey,
    JsonElement? Arguments,
    string ClientMessageId);

public sealed record ActionRejectBody(string? Reason);

public sealed record ActionResultDto(
    string RequestId,
    string Status,
    string Code,
    string? ExternalId,
    string? ErrorCode,
    JsonElement Data);
