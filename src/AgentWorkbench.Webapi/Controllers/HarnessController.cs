using AgentWorkbench.AgentHarness;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

public sealed record HarnessRunBody(string? SessionId, string? Input);

[ApiController]
[Route("api/harness")]
public sealed class HarnessController : ControllerBase
{
    private readonly IAgentHarness _harness;

    public HarnessController(IAgentHarness harness) => _harness = harness;

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
        => Ok(await _harness.GetCapabilitiesAsync(cancellationToken));

    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] HarnessRunBody body, CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Input))
            return BadRequest(new { message = "input 不能为空" });

        try
        {
            var result = await _harness.RunAsync(
                new HarnessRunRequest(body.SessionId, body.Input!),
                cancellationToken);
            return Ok(result);
        }
        catch (HarnessModelNotConfiguredException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (TimeoutException ex)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { message = ex.Message });
        }
    }

    [HttpGet("runs/{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var result = await _harness.GetRunAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
