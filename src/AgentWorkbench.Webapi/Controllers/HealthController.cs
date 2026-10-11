using AgentWorkbench.Conversation;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class HealthController : ControllerBase
{
    private readonly IChatService _chat;

    public HealthController(IChatService chat) => _chat = chat;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) => Ok(new
    {
        status = "ok",
        time = DateTimeOffset.UtcNow,
        modelConfigured = await _chat.IsConfiguredAsync(cancellationToken)
    });
}
