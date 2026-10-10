using System.Diagnostics;
using System.Text.Json;
using AgentWorkbench.Agent;
using AgentWorkbench.Review.Comparison;
using AgentWorkbench.Review.Extraction;

namespace AgentWorkbench.Review;

/// <summary>审查服务：创建 / 查询 / 执行（要素抽取 + 规则比对）。</summary>
public interface IReviewService
{
    Task<ReviewCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);

    Task<long> CreateRunAsync(string documentId, string mode, string? template, CancellationToken cancellationToken);

    Task<ReviewRunRecord?> GetRunAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewRunRecord>> ListRunsAsync(string? documentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewElementRecord>> GetElementsAsync(long runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewFindingRecord>> GetFindingsAsync(long runId, CancellationToken cancellationToken);

    Task<int> CountElementsAsync(long runId, CancellationToken cancellationToken);

    Task<int> CountFindingsAsync(long runId, CancellationToken cancellationToken);

    Task<string> GetReportAsync(long runId, string format, CancellationToken cancellationToken);

    Task<bool> DeleteRunAsync(long runId, CancellationToken cancellationToken);

    /// <summary>重跑：重置状态并重新入队。</summary>
    Task<bool> RerunAsync(long runId, CancellationToken cancellationToken);

    /// <summary>后台执行一次审查（由 worker 调用）。</summary>
    Task ExecuteAsync(long runId, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class ReviewService : IReviewService
{
    private readonly IReviewRepository _review;
    private readonly IDocumentRepository _documents;
    private readonly IRuleRepository _rules;
    private readonly ElementExtractorRegistry _extractors;
    private readonly RuleCheckerRegistry _checkers;
    private readonly IStructuredChatService _chat;
    private readonly IReviewReportWriter _reportWriter;
    private readonly IReviewRunQueue _queue;
    private readonly IReviewProgressNotifier _notifier;
    private readonly ILogger<ReviewService> _logger;

    public ReviewService(
        IReviewRepository review,
        IDocumentRepository documents,
        IRuleRepository rules,
        ElementExtractorRegistry extractors,
        RuleCheckerRegistry checkers,
        IStructuredChatService chat,
        IReviewReportWriter reportWriter,
        IReviewRunQueue queue,
        IReviewProgressNotifier notifier,
        ILogger<ReviewService> logger)
    {
        _review = review;
        _documents = documents;
        _rules = rules;
        _extractors = extractors;
        _checkers = checkers;
        _chat = chat;
        _reportWriter = reportWriter;
        _queue = queue;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<ReviewCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
        => new(true, true, await _chat.IsConfiguredAsync(cancellationToken));

    public async Task<long> CreateRunAsync(string documentId, string mode, string? template, CancellationToken cancellationToken)
    {
        var document = await _documents.GetAsync(documentId)
            ?? throw new ReviewDocumentNotFoundException(documentId);

        if (!string.Equals(document.Status, DocumentStatus.Parsed, StringComparison.Ordinal))
            throw new ReviewDocumentNotReadyException(document.Id, document.Status);

        var normalized = Normalize(mode);
        var run = await _review.CreateRunAsync(documentId, normalized, template);
        await _queue.EnqueueAsync(run.Id, cancellationToken);
        _logger.LogInformation("Created review run {RunId} for document {DocumentId} mode={Mode}", run.Id, documentId, normalized);
        return run.Id;
    }

    public Task<ReviewRunRecord?> GetRunAsync(long id, CancellationToken cancellationToken)
        => _review.GetRunAsync(id);

    public Task<IReadOnlyList<ReviewRunRecord>> ListRunsAsync(string? documentId, CancellationToken cancellationToken)
        => _review.ListRunsAsync(documentId);

    public Task<IReadOnlyList<ReviewElementRecord>> GetElementsAsync(long runId, CancellationToken cancellationToken)
        => _review.GetElementsAsync(runId);

    public Task<IReadOnlyList<ReviewFindingRecord>> GetFindingsAsync(long runId, CancellationToken cancellationToken)
        => _review.GetFindingsAsync(runId);

    public Task<int> CountElementsAsync(long runId, CancellationToken cancellationToken)
        => _review.CountElementsAsync(runId);

    public Task<int> CountFindingsAsync(long runId, CancellationToken cancellationToken)
        => _review.CountFindingsAsync(runId);

    public async Task<string> GetReportAsync(long runId, string format, CancellationToken cancellationToken)
    {
        var run = await _review.GetRunAsync(runId)
            ?? throw new ReviewRunNotFoundException(runId);
        var document = await _documents.GetAsync(run.DocumentId)
            ?? throw new ReviewDocumentNotFoundException(run.DocumentId);
        var elements = await _review.GetElementsAsync(runId);
        var findings = await _review.GetFindingsAsync(runId);

        return (format ?? "md").ToLowerInvariant() switch
        {
            "csv" => _reportWriter.WriteCsv(run, document, elements, findings),
            "json" => JsonSerializer.Serialize(new { run, document = new { document.Id, document.FileName }, elements, findings },
                new JsonSerializerOptions { WriteIndented = true }),
            _ => _reportWriter.WriteMarkdown(run, document, elements, findings)
        };
    }

    public Task<bool> DeleteRunAsync(long runId, CancellationToken cancellationToken)
        => _review.DeleteRunAsync(runId);

    public async Task<bool> RerunAsync(long runId, CancellationToken cancellationToken)
    {
        var run = await _review.GetRunAsync(runId);
        if (run is null)
            return false;

        run.Status = ReviewStatuses.Pending;
        run.Steps = "[]";
        run.Error = null;
        run.LatencyMs = null;
        run.FinishedAt = null;
        await _review.UpdateRunAsync(run);
        await _queue.EnqueueAsync(run.Id, cancellationToken);
        _logger.LogInformation("Re-enqueued review run {RunId}", runId);
        return true;
    }

    public async Task ExecuteAsync(long runId, CancellationToken cancellationToken)
    {
        var run = await _review.GetRunAsync(runId);
        if (run is null)
        {
            _logger.LogWarning("Review run {RunId} not found, skip", runId);
            return;
        }

        var document = await _documents.GetAsync(run.DocumentId);
        if (document is null)
        {
            await FinishAsync(run, ReviewStatuses.Failed, [], null, "文档不存在", cancellationToken);
            return;
        }

        var chunks = await _documents.GetChunksAsync(run.DocumentId);
        var context = new ReviewContext(document.Id, document.FileName, chunks);
        var steps = new List<StepState>();
        var total = Stopwatch.StartNew();

        run.Status = ReviewStatuses.Running;
        run.Steps = JsonSerializer.Serialize(steps);
        await _review.UpdateRunAsync(run);
        await _notifier.NotifyAsync(run.Id, run.Status, run.Steps, cancellationToken);

        try
        {
            var elements = new List<ElementDraft>();

            if (Includes(run.Mode, ReviewModes.Extract) || Includes(run.Mode, ReviewModes.Compare))
            {
                var step = new StepState("extract", "running", 0, null);
                steps.Add(step);
                await SaveProgressAsync(run, steps, cancellationToken);

                var sw = Stopwatch.StartNew();
                elements = (await _extractors.ExtractAllAsync(context, cancellationToken)).ToList();
                var llmAvailable = await _chat.IsConfiguredAsync(cancellationToken);
                if (!llmAvailable)
                    step = step with { Detail = "LLM 未配置，仅规则抽取" };

                if (Includes(run.Mode, ReviewModes.Extract))
                    await _review.ReplaceElementsAsync(run.Id, elements.Select(e => ToElement(run.Id, e)).ToList());

                step = step with { Status = "succeeded", ElapsedMs = sw.ElapsedMilliseconds, Detail = step.Detail ?? $"抽取 {elements.Count} 项" };
                steps[^1] = step;
                await SaveProgressAsync(run, steps, cancellationToken);
            }

            if (Includes(run.Mode, ReviewModes.Compare))
            {
                var step = new StepState("compare", "running", 0, null);
                steps.Add(step);
                await SaveProgressAsync(run, steps, cancellationToken);

                var sw = Stopwatch.StartNew();
                var rules = await LoadRulesAsync(run.Template, cancellationToken);
                var relevant = DocumentRelevance.LooksLikeContract(context.FullText);
                var findings = relevant
                    ? _checkers.CheckAll(context, rules, elements).ToList()
                    : new List<FindingDraft>();
                await _review.ReplaceFindingsAsync(run.Id, findings.Select(f => ToFinding(run.Id, f)).ToList());
                step = step with
                {
                    Status = "succeeded",
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Detail = relevant ? $"命中 {findings.Count} 项（规则 {rules.Count}）" : "非合同文本，跳过规则比对"
                };
                steps[^1] = step;
                await SaveProgressAsync(run, steps, cancellationToken);
            }

            await FinishAsync(run, ReviewStatuses.Succeeded, steps, total.ElapsedMilliseconds, null, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Status = ReviewStatuses.Failed;
            run.Error = "已取消";
            run.Steps = JsonSerializer.Serialize(steps);
            run.LatencyMs = total.ElapsedMilliseconds;
            run.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
            await _review.UpdateRunAsync(run);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "审查 run {RunId} 执行失败", runId);
            await FinishAsync(run, ReviewStatuses.Failed, steps, total.ElapsedMilliseconds, ex.Message, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<RuleRecord>> LoadRulesAsync(string? template, CancellationToken cancellationToken)
    {
        var rules = await _rules.ListEnabledAsync();
        if (string.IsNullOrWhiteSpace(template))
            return rules;
        return rules.Where(r => string.Equals(r.Category, template, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task FinishAsync(
        ReviewRunRecord run, string status, List<StepState> steps, long? latencyMs, string? error, CancellationToken cancellationToken)
    {
        run.Status = status;
        run.Error = error;
        run.Steps = JsonSerializer.Serialize(steps);
        run.LatencyMs = latencyMs;
        run.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
        await _review.UpdateRunAsync(run);
        await _notifier.NotifyAsync(run.Id, run.Status, run.Steps, cancellationToken);
        _logger.LogInformation("Review run {RunId} finished: {Status} in {LatencyMs}ms", run.Id, status, latencyMs);
    }

    private async Task SaveProgressAsync(ReviewRunRecord run, List<StepState> steps, CancellationToken cancellationToken)
    {
        run.Steps = JsonSerializer.Serialize(steps);
        await _review.UpdateRunAsync(run);
        await _notifier.NotifyAsync(run.Id, run.Status, run.Steps, cancellationToken);
    }

    private static bool Includes(string mode, string step)
        => string.Equals(mode, ReviewModes.Both, StringComparison.OrdinalIgnoreCase)
           || string.Equals(mode, step, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? mode) => mode?.ToLowerInvariant() switch
    {
        ReviewModes.Extract => ReviewModes.Extract,
        ReviewModes.Compare => ReviewModes.Compare,
        ReviewModes.Both => ReviewModes.Both,
        _ => throw new ReviewInvalidModeException(mode ?? string.Empty)
    };

    private static ReviewElementRecord ToElement(long runId, ElementDraft e) => new()
    {
        RunId = runId,
        Key = e.Key,
        Label = e.Label,
        Value = e.Value,
        ValueType = e.ValueType,
        Unit = e.Unit,
        Source = e.Source,
        Confidence = e.Confidence,
        ChunkId = e.ChunkId,
        Seq = e.Seq,
        PageFrom = e.PageFrom,
        PageTo = e.PageTo,
        Evidence = e.Evidence,
        CreatedAt = DateTimeOffset.UtcNow.ToString("o")
    };

    private static ReviewFindingRecord ToFinding(long runId, FindingDraft f) => new()
    {
        RunId = runId,
        RuleId = f.RuleId,
        Severity = f.Severity,
        Category = f.Category,
        Title = f.Title,
        Detail = f.Detail,
        Expected = f.Expected,
        Actual = f.Actual,
        ChunkId = f.ChunkId,
        Seq = f.Seq,
        PageFrom = f.PageFrom,
        PageTo = f.PageTo,
        Evidence = f.Evidence,
        CreatedAt = DateTimeOffset.UtcNow.ToString("o")
    };

    private sealed record StepState(string Name, string Status, long ElapsedMs, string? Detail);
}

/// <summary>审查任务不存在。</summary>
public sealed class ReviewRunNotFoundException(long runId) : Exception($"审查任务不存在：{runId}");
