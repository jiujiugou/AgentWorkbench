using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

public sealed record CreateConversationRequest(string? Title);

/// <summary>会话 CRUD（通用聊天上下文持久化：列表 / 历史 / 新建 / 删除）。</summary>
[ApiController]
[Route("api/conversations")]
public sealed class ConversationsController : ControllerBase
{
    private readonly IConversationRepository _repository;

    public ConversationsController(IConversationRepository repository) => _repository = repository;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
        => Ok((await _repository.ListAsync()).Select(ToView));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConversationRequest? body, CancellationToken cancellationToken)
    {
        var conversation = await _repository.CreateAsync(body?.Title);
        return Ok(ToView(conversation));
    }

    [HttpGet("{id}/messages")]
    public async Task<IActionResult> Messages(string id, CancellationToken cancellationToken)
    {
        if (await _repository.GetAsync(id) is null)
            return NotFound();
        var messages = await _repository.GetMessagesAsync(id);
        return Ok(messages.Select(m => new { m.Id, m.Role, m.Content, m.CreatedAt }));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken cancellationToken)
        => await _repository.DeleteAsync(id) ? NoContent() : NotFound();

    private static object ToView(ConversationRecord c) => new { c.Id, c.Title, c.CreatedAt, c.UpdatedAt };
}
