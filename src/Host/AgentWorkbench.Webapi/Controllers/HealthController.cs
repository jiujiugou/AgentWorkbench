using AgentWorkbench.Knowledge.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

/// <remarks>健康检查是唯一匿名端点：探活/编排器在登录前就要能问「你活着吗」。</remarks>
[ApiController]
[Route("api/[controller]")]
public sealed class HealthController : ControllerBase
{
    private readonly IChatService _chat;

    public HealthController(IChatService chat) => _chat = chat;

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) => Ok(new
    {
        status = "ok",
        time = DateTimeOffset.UtcNow,
        modelConfigured = await _chat.IsConfiguredAsync(cancellationToken)
    });
}
