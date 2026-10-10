using AgentWorkbench.Workflow;
using AgentWorkbench.Workflow.Prompting;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

public sealed record CreateWorkflowRunRequest(string DocumentId, string? WorkflowKey, string? PromptVersion);
public sealed record AddPromptRequest(string PromptKey, string Version, string Content, bool? Active);

/// <summary>P4 工作流：定义 / 运行 / 步骤 / 单步重跑 / 取消 / 恢复 / 提示词版本。</summary>
[ApiController]
[Route("api/workflow")]
public sealed class WorkflowController : ControllerBase
{
    private readonly IWorkflowService _service;
    private readonly IPromptStore _prompts;

    public WorkflowController(IWorkflowService service, IPromptStore prompts)
    {
        _service = service;
        _prompts = prompts;
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
        => Ok(await _service.GetCapabilitiesAsync(cancellationToken));

    [HttpGet("definitions")]
    public async Task<IActionResult> Definitions(CancellationToken cancellationToken)
        => Ok(await _service.ListDefinitionsAsync(cancellationToken));

    [HttpPost("runs")]
    public async Task<IActionResult> Create([FromBody] CreateWorkflowRunRequest body, CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.DocumentId))
            return BadRequest(new { message = "documentId 不能为空" });

        try
        {
            var runId = await _service.CreateRunAsync(body.DocumentId, body.WorkflowKey, body.PromptVersion, cancellationToken);
            return Accepted(new { runId });
        }
        catch (WorkflowDocumentNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (WorkflowDefinitionNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (WorkflowDocumentNotReadyException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (WorkflowPromptNotFoundException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("runs")]
    public async Task<IActionResult> List([FromQuery] string? documentId, CancellationToken cancellationToken)
        => Ok(await _service.ListRunsAsync(documentId, cancellationToken));

    [HttpGet("runs/{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var run = await _service.GetRunAsync(id, cancellationToken);
        return run is null ? NotFound() : Ok(run);
    }

    [HttpGet("runs/{id:long}/steps")]
    public async Task<IActionResult> Steps(long id, CancellationToken cancellationToken)
    {
        var run = await _service.GetRunAsync(id, cancellationToken);
        return run is null ? NotFound() : Ok(run.Steps);
    }

    [HttpPost("runs/{id:long}/steps/{stepKey}/rerun")]
    public async Task<IActionResult> Rerun(long id, string stepKey, CancellationToken cancellationToken)
    {
        try
        {
            await _service.RerunStepAsync(id, stepKey, cancellationToken);
            return Accepted(new { runId = id, stepKey });
        }
        catch (WorkflowRunNotFoundException)
        {
            return NotFound();
        }
        catch (WorkflowStepNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpPost("runs/{id:long}/cancel")]
    public async Task<IActionResult> Cancel(long id, CancellationToken cancellationToken)
        => await _service.CancelAsync(id, cancellationToken) ? Accepted(new { runId = id }) : NotFound();

    [HttpPost("runs/{id:long}/resume")]
    public async Task<IActionResult> Resume(long id, CancellationToken cancellationToken)
    {
        try
        {
            await _service.ResumeAsync(id, cancellationToken);
            return Accepted(new { runId = id });
        }
        catch (WorkflowRunNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("runs/{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
        => await _service.DeleteRunAsync(id, cancellationToken) ? NoContent() : NotFound();

    [HttpGet("prompts")]
    public async Task<IActionResult> Prompts([FromQuery] string? key, CancellationToken cancellationToken)
        => Ok(await _prompts.ListAsync(key, cancellationToken));

    [HttpPost("prompts")]
    public async Task<IActionResult> AddPrompt([FromBody] AddPromptRequest body, CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.PromptKey) || string.IsNullOrWhiteSpace(body.Version))
            return BadRequest(new { message = "promptKey/version 不能为空" });

        var record = await _prompts.AddAsync(body.PromptKey, body.Version, body.Content ?? string.Empty, body.Active ?? false, cancellationToken);
        return Ok(record);
    }

    [HttpPost("prompts/{key}/{version}/activate")]
    public async Task<IActionResult> ActivatePrompt(string key, string version, CancellationToken cancellationToken)
        => await _prompts.ActivateAsync(key, version, cancellationToken) ? NoContent() : NotFound();
}
