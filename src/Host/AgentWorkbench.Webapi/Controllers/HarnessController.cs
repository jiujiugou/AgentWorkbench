using AgentWorkbench.Runtime.Harness;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

/// <summary>
/// Harness 的只读观察口：能力位与单次 run 的结果。
///
/// <para>
/// <b>这里没有「起 run」的端点，而且不该有。</b> 起 run 的唯一产品入口是 SignalR
/// <c>/hubs/agent</c>（<c>Send</c> / <c>Ask</c> / <c>Stop</c>）：它带流式增量、工具完成事件、
/// 会话复用与停止能力；而一个 HTTP <c>POST .../run</c> 两者都没有，一旦存在就会长出第二条
/// 对话路径——客户端绕过输入级去重、界面拿不到增量、<c>Stop</c> 找不到目标，
/// 同时还要单独维护一份身份作用域的建立与校验。
/// </para>
/// <para>
/// 需要程序化起一次 run（脚本、验收、探针）时：产品路径走 Hub；
/// 测试内直接调 <see cref="IAgentHarness.RunAsync"/>，别再加 HTTP 壳。
/// </para>
/// </summary>
[ApiController]
[Route("api/harness")]
public sealed class HarnessController : ControllerBase
{
    private readonly IAgentHarness _harness;

    public HarnessController(IAgentHarness harness) => _harness = harness;

    /// <summary>能力位：loop / llm / context / memory 是否就绪，以及当前可见的工具清单。</summary>
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
        => Ok(await _harness.GetCapabilitiesAsync(cancellationToken));

    /// <summary>按 run id 读单次运行结果（状态、迭代数、耗时、步骤）。</summary>
    [HttpGet("runs/{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var result = await _harness.GetRunAsync(id, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
