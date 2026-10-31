using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using AgentWorkbench.Webapi.Auth;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

public sealed record CreateConversationRequest(string? Title);

/// <summary>会话 CRUD（通用聊天上下文持久化：列表 / 历史 / 新建 / 删除）。</summary>
/// <remarks>
/// 每个动作都带「当前租户 + 当前用户」：会话归属是隔离的最小单元。
/// 归属来自 cookie 里的 claim，不来自请求体——请求体里的 id 只表达「我想接着哪个会话」，
/// 是否真属于我由仓储判定（不属于就当作不存在，既不报错也不泄露存在性）。
/// </remarks>
[ApiController]
[Route("api/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationRepository _repository;

    public ConversationsController(IConversationRepository repository) => _repository = repository;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var (tenantId, userId) = User.Owner();
        return Ok((await _repository.ListAsync(tenantId, userId)).Select(ToView));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConversationRequest? body, CancellationToken cancellationToken)
    {
        var (tenantId, userId) = User.Owner();
        var conversation = await _repository.CreateAsync(tenantId, userId, body?.Title);
        return Ok(ToView(conversation));
    }

    [HttpGet("{id}/messages")]
    public async Task<IActionResult> Messages(string id, CancellationToken cancellationToken)
    {
        var (tenantId, userId) = User.Owner();
        if (await _repository.GetAsync(id, tenantId, userId) is null)
            return NotFound();
        var messages = await _repository.GetMessagesAsync(id);
        return Ok(messages.Select(m => new { m.Id, m.Role, m.Content, m.CreatedAt }));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var (tenantId, userId) = User.Owner();
        return await _repository.DeleteAsync(id, tenantId, userId) ? NoContent() : NotFound();
    }

    private static object ToView(ConversationRecord c) => new { c.Id, c.Title, c.CreatedAt, c.UpdatedAt };
}
