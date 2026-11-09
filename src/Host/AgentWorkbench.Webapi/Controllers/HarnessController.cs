using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Webapi.Actions;
using AgentWorkbench.Webapi.Problems;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

/// <summary>
/// 提交体。<c>ClientMessageId</c> 必填：本入口与 Hub 一样会起工具环，
/// 模型在里面调 <c>propose_action</c> 时必须有「这是哪一次输入」才能派生出稳定请求号。
/// </summary>
public sealed record HarnessRunBody(string? SessionId, string? Input, string ClientMessageId);

[ApiController]
[Route("api/harness")]
public sealed class HarnessController : ControllerBase
{
    private readonly IAgentHarness _harness;
    private readonly IActionSubjectScope _subjectScope;

    public HarnessController(IAgentHarness harness, IActionSubjectScope subjectScope)
    {
        _harness = harness;
        _subjectScope = subjectScope;
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
        => Ok(await _harness.GetCapabilitiesAsync(cancellationToken));

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] HarnessRunBody body, CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Input))
            return this.Result(new ApiError(
                StatusCodes.Status400BadRequest, "invalid_input", "input 不能为空"));
        if (!InputIdentity.IsPresent(body.ClientMessageId))
            return this.Result(new ApiError(
                StatusCodes.Status400BadRequest, InputIdentity.MissingCode,
                "缺少本次输入的标识，请求号无法稳定派生"));

        // 无会话时由 Host 生成，与原先 harness 内部生成随机 id 语义等价（每 run 隔离）。
        // 用同一个值建作用域：工具环里的 IActionOriginProvider 才知道「这是哪一次输入」。
        var sessionId = string.IsNullOrWhiteSpace(body.SessionId)
            ? Guid.NewGuid().ToString("N")
            : body.SessionId!;

        try
        {
            using var subjectScope = _subjectScope.Begin(
                User.Identity?.Name, sessionId, body.ClientMessageId);
            var result = await _harness.RunAsync(
                new HarnessRunRequest(sessionId, body.Input!),
                cancellationToken);
            return Ok(result);
        }
        catch (HarnessModelNotConfiguredException ex)
        {
            return this.Result(new ApiError(
                StatusCodes.Status400BadRequest, "model_not_configured", ex.Message));
        }
        catch (TimeoutException ex)
        {
            return this.Result(new ApiError(
                StatusCodes.Status504GatewayTimeout, "harness_timeout", ex.Message)
            { Retryable = true });
        }
    }

    [HttpGet("runs/{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var result = await _harness.GetRunAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
