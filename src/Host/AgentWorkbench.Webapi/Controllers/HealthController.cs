using AgentWorkbench.Runtime.Harness;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

/// <remarks>
/// 健康检查是唯一匿名端点：探活/编排器在登录前就要能问「你活着吗」。
/// <para>
/// 模型可用性取自 <see cref="IAgentHarness.GetCapabilitiesAsync"/> 的 <c>Llm</c> 位——
/// 它问的是同一件事（有没有可解析的模型端点）。此前这里另有一个只做这一件事的
/// <c>IChatService</c>，那是 v1 第二条生成路径拆掉后剩下的 24 行空壳，已并入能力位。
/// </para>
/// </remarks>
[ApiController]
[Route("api/[controller]")]
public sealed class HealthController : ControllerBase
{
    private readonly IAgentHarness _harness;

    public HealthController(IAgentHarness harness) => _harness = harness;

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var capabilities = await _harness.GetCapabilitiesAsync(cancellationToken);
        return Ok(new
        {
            status = "ok",
            time = DateTimeOffset.UtcNow,
            modelConfigured = capabilities.Llm
        });
    }
}
