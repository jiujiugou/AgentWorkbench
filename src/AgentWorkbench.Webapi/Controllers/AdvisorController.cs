using AgentWorkbench.Domain;
using AgentWorkbench.ReviewAdvisor;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

[ApiController]
[Route("api")]
public sealed class AdvisorController : ControllerBase
{
    private readonly IReviewAdvisorService _service;

    public AdvisorController(IReviewAdvisorService service) => _service = service;

    [HttpGet("reviewadvisor/health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
        => Ok(await _service.GetCapabilitiesAsync(cancellationToken));

    [HttpPost("review/runs/{id:long}/advisor")]
    public async Task<IActionResult> Create(long id, CancellationToken cancellationToken)
    {
        try
        {
            await _service.CreateJobAsync(id, cancellationToken);
            return Accepted(new { runId = id });
        }
        catch (AdvisorRunNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (AdvisorRunNotReadyException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (AdvisorLlmNotConfiguredException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("review/runs/{id:long}/advisor")]
    public async Task<IActionResult> Status(long id, CancellationToken cancellationToken)
    {
        var job = await _service.GetJobAsync(id, cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpGet("review/runs/{id:long}/conclusions")]
    public async Task<IActionResult> Conclusions(long id, CancellationToken cancellationToken)
    {
        var conclusions = await _service.GetConclusionsAsync(id, cancellationToken);
        return Ok(conclusions.Select(ToView));
    }

    [HttpGet("review/runs/{id:long}/advisor/trace")]
    public async Task<IActionResult> Trace(long id, CancellationToken cancellationToken)
    {
        var steps = await _service.GetStepsAsync(id, cancellationToken);
        return Ok(steps.Select(s => new ReviewAgentStepView(
            s.Id, s.FindingId, s.StepIndex, s.Kind, s.Tool, s.Arguments, s.Result)));
    }

    private static ReviewConclusionView ToView(ReviewConclusionRecord c) => new(
        c.Id, c.FindingId, c.Status, c.Title, c.Conclusion, c.Suggestion, c.Impact,
        c.Basis, c.Confidence, c.Source, c.ChunkId, c.Seq, c.PageFrom, c.PageTo,
        c.Evidence, c.Citations, c.StepsUsed);
}
