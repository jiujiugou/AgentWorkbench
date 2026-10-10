using System.Diagnostics;
using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using AgentWorkbench.Review;
using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Controllers;

public sealed record CreateReviewRuleRequest(
    string Category, string Name, string? Body, string? RuleType, string? Severity,
    string? TargetKey, string? Expected, string? MatchHint);

[ApiController]
[Route("api/review")]
public sealed class ReviewController : ControllerBase
{
    private readonly IReviewService _service;
    private readonly IDocumentRepository _documents;
    private readonly ILogger<ReviewController> _logger;

    public ReviewController(IReviewService service, IDocumentRepository documents, ILogger<ReviewController> logger)
    {
        _service = service;
        _documents = documents;
        _logger = logger;
    }

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
        => Ok(await _service.GetCapabilitiesAsync(cancellationToken));

    [HttpPost("runs")]
    public async Task<IActionResult> Create([FromBody] CreateReviewRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DocumentId))
            return BadRequest(new { message = "documentId 不能为空" });

        var sw = Stopwatch.StartNew();
        try
        {
            var runId = await _service.CreateRunAsync(request.DocumentId, request.Mode ?? ReviewModes.Both, request.Template, cancellationToken);
            _logger.LogInformation("Create review run {RunId} in {ElapsedMs}ms", runId, sw.ElapsedMilliseconds);
            return Accepted(new { runId });
        }
        catch (ReviewDocumentNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ReviewDocumentNotReadyException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (ReviewInvalidModeException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("runs")]
    public async Task<IActionResult> List([FromQuery] string? documentId, CancellationToken cancellationToken)
    {
        var runs = await _service.ListRunsAsync(documentId, cancellationToken);
        var result = new List<ReviewRunView>(runs.Count);
        foreach (var run in runs)
            result.Add(await ToViewAsync(run, cancellationToken));
        return Ok(result);
    }

    [HttpGet("runs/{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var run = await _service.GetRunAsync(id, cancellationToken);
        if (run is null)
            return NotFound();
        return Ok(await ToViewAsync(run, cancellationToken));
    }

    [HttpPost("runs/{id:long}/rerun")]
    public async Task<IActionResult> Rerun(long id, CancellationToken cancellationToken)
    {
        var ok = await _service.RerunAsync(id, cancellationToken);
        return ok ? Accepted(new { runId = id }) : NotFound();
    }

    [HttpDelete("runs/{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var ok = await _service.DeleteRunAsync(id, cancellationToken);
        return ok ? NoContent() : NotFound();
    }

    [HttpGet("runs/{id:long}/elements")]
    public async Task<IActionResult> Elements(long id, CancellationToken cancellationToken)
    {
        var elements = await _service.GetElementsAsync(id, cancellationToken);
        return Ok(elements.Select(ToElementView));
    }

    [HttpGet("runs/{id:long}/findings")]
    public async Task<IActionResult> Findings(long id, CancellationToken cancellationToken)
    {
        var findings = await _service.GetFindingsAsync(id, cancellationToken);
        return Ok(findings.Select(ToFindingView));
    }

    [HttpGet("runs/{id:long}/report")]
    public async Task<IActionResult> Report(long id, string? format, CancellationToken cancellationToken)
    {
        try
        {
            var content = await _service.GetReportAsync(id, format ?? "md", cancellationToken);
            return Ok(new { format = format ?? "md", content });
        }
        catch (ReviewRunNotFoundException)
        {
            return NotFound();
        }
        catch (ReviewDocumentNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    [HttpGet("rules")]
    public async Task<IActionResult> Rules([FromServices] IRuleRepository rules)
        => Ok(await rules.ListAsync());

    [HttpPost("rules")]
    public async Task<IActionResult> CreateRule([FromBody] CreateReviewRuleRequest request, [FromServices] IRuleRepository rules)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "name 不能为空" });

        var rule = new RuleRecord
        {
            Category = string.IsNullOrWhiteSpace(request.Category) ? "买卖合同" : request.Category,
            Name = request.Name,
            Body = request.Body ?? string.Empty,
            RuleType = request.RuleType ?? RuleTypes.ClauseRequired,
            Severity = request.Severity ?? FindingSeverities.Medium,
            TargetKey = request.TargetKey,
            Expected = request.Expected,
            MatchHint = request.MatchHint
        };
        return Ok(await rules.InsertAsync(rule));
    }

    [HttpDelete("rules/{id:long}")]
    public async Task<IActionResult> DeleteRule(long id, [FromServices] IRuleRepository rules)
        => await rules.DeleteAsync(id) ? NoContent() : NotFound();

    private async Task<ReviewRunView> ToViewAsync(ReviewRunRecord run, CancellationToken cancellationToken)
    {
        var document = await _documents.GetAsync(run.DocumentId);
        var elementCount = await _service.CountElementsAsync(run.Id, cancellationToken);
        var findingCount = await _service.CountFindingsAsync(run.Id, cancellationToken);
        return new ReviewRunView(
            run.Id, run.DocumentId, document?.FileName ?? run.DocumentId, run.Mode, run.Template,
            run.Status, run.Model, run.Error, run.LatencyMs, run.CreatedAt, run.FinishedAt,
            elementCount, findingCount, run.Steps);
    }

    private static ReviewElementView ToElementView(ReviewElementRecord e) => new(
        e.Id, e.Key, e.Label, e.Value, e.ValueType, e.Unit, e.Source, e.Confidence,
        e.ChunkId, e.Seq, e.PageFrom, e.PageTo, e.Evidence);

    private static ReviewFindingView ToFindingView(ReviewFindingRecord f) => new(
        f.Id, f.RuleId, f.Severity, f.Category, f.Title, f.Detail, f.Expected, f.Actual,
        f.ChunkId, f.Seq, f.PageFrom, f.PageTo, f.Evidence);
}
