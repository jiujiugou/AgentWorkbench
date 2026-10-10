using System.Diagnostics;
using AgentWorkbench.ReviewAdvisor.Agent;
using AgentWorkbench.ReviewAdvisor.Evidence;

namespace AgentWorkbench.ReviewAdvisor;

public interface IReviewAdvisorService
{
    Task<AdvisorCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<AdvisorJobRecord> CreateJobAsync(long runId, CancellationToken cancellationToken = default);

    Task<AdvisorJobView?> GetJobAsync(long runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReviewConclusionRecord>> GetConclusionsAsync(long runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReviewAgentStepRecord>> GetStepsAsync(long runId, CancellationToken cancellationToken = default);

    Task ExecuteAsync(long runId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ReviewAdvisorService : IReviewAdvisorService
{
    private readonly IReviewService _review;
    private readonly IDocumentRepository _documents;
    private readonly IKnowledgeEngine _knowledge;
    private readonly IStructuredChatService _chat;
    private readonly IAdvisorRepository _repository;
    private readonly ReviewAgentLoop _loop;
    private readonly IAdvisorRunQueue _queue;
    private readonly ILogger<ReviewAdvisorService> _logger;

    public ReviewAdvisorService(
        IReviewService review,
        IDocumentRepository documents,
        IKnowledgeEngine knowledge,
        IStructuredChatService chat,
        IAdvisorRepository repository,
        ReviewAgentLoop loop,
        IAdvisorRunQueue queue,
        ILogger<ReviewAdvisorService> logger)
    {
        _review = review;
        _documents = documents;
        _knowledge = knowledge;
        _chat = chat;
        _repository = repository;
        _loop = loop;
        _queue = queue;
        _logger = logger;
    }

    public async Task<AdvisorCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        // advisor 能力需要模型：未配置模型时 advisor=false（不产结论、不挂起）。llm 反映模型是否配置。
        var llm = await _chat.IsConfiguredAsync(cancellationToken);
        return new(llm, llm);
    }

    public async Task<AdvisorJobRecord> CreateJobAsync(long runId, CancellationToken cancellationToken = default)
    {
        var run = await _review.GetRunAsync(runId, cancellationToken)
            ?? throw new AdvisorRunNotFoundException(runId);

        if (!string.Equals(run.Status, ReviewStatuses.Succeeded, StringComparison.Ordinal))
            throw new AdvisorRunNotReadyException(runId, run.Status);

        if (!await _chat.IsConfiguredAsync(cancellationToken))
            throw new AdvisorLlmNotConfiguredException();

        var job = await _repository.UpsertJobAsync(runId);
        await _queue.EnqueueAsync(runId, cancellationToken);
        return job;
    }

    public async Task<AdvisorJobView?> GetJobAsync(long runId, CancellationToken cancellationToken = default)
    {
        var job = await _repository.GetJobAsync(runId);
        if (job is null)
            return null;
        var count = await _repository.CountConclusionsAsync(runId);
        return new AdvisorJobView(job.Status, job.Error, count, job.CreatedAt, job.FinishedAt);
    }

    public Task<IReadOnlyList<ReviewConclusionRecord>> GetConclusionsAsync(long runId, CancellationToken cancellationToken = default)
        => _repository.GetConclusionsAsync(runId);

    public Task<IReadOnlyList<ReviewAgentStepRecord>> GetStepsAsync(long runId, CancellationToken cancellationToken = default)
        => _repository.GetStepsAsync(runId);

    public async Task ExecuteAsync(long runId, CancellationToken cancellationToken = default)
    {
        var job = await _repository.GetJobAsync(runId);
        if (job is null)
            return;

        var total = Stopwatch.StartNew();
        job.Status = AdvisorJobStatuses.Running;
        await _repository.UpdateJobAsync(job);

        try
        {
            var run = await _review.GetRunAsync(runId, cancellationToken);
            if (run is null)
            {
                await FailAsync(job, "审查任务不存在");
                return;
            }

            var chunks = await _documents.GetChunksAsync(run.DocumentId);
            var findings = await _review.GetFindingsAsync(runId, cancellationToken);
            var context = new AdvisorToolContext { DocumentId = run.DocumentId, Chunks = chunks, Knowledge = _knowledge };

            var conclusions = new List<ReviewConclusionRecord>();
            var steps = new List<ReviewAgentStepRecord>();

            foreach (var finding in findings)
            {
                var (observations, hasEvidence) = await GatherInitialEvidenceAsync(finding, context, cancellationToken);
                AdvisorLoopResult result;
                if (!hasEvidence)
                {
                    var draft = NoEvidence(finding);
                    result = new AdvisorLoopResult(draft,
                        [new AdvisorStepDraft(finding.Id, 0, "assistant", null, null, "无证据，未调用模型")]);
                }
                else
                {
                    result = await _loop.RunAsync(finding, context, observations, ReviewAgentLoop.DefaultMaxSteps, cancellationToken);
                }

                conclusions.Add(ToRecord(runId, result.Conclusion));
                steps.AddRange(result.Steps.Select(s => ToRecord(runId, s)));
            }

            await _repository.ReplaceConclusionsAsync(runId, conclusions);
            await _repository.ReplaceStepsAsync(runId, steps);

            job.Status = AdvisorJobStatuses.Succeeded;
            job.Error = null;
            job.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
            await _repository.UpdateJobAsync(job);
            _logger.LogInformation("Advisor run {RunId} finished: {Conclusions} conclusions in {ElapsedMs}ms",
                runId, conclusions.Count, total.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FailAsync(job, "已取消");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Advisor run {RunId} 执行失败", runId);
            await FailAsync(job, ex.Message);
        }
    }

    private async Task<(List<string> observations, bool hasEvidence)> GatherInitialEvidenceAsync(
        ReviewFindingRecord finding, AdvisorToolContext context, CancellationToken cancellationToken)
    {
        var observations = new List<string>();
        var query = BuildQuery(finding);

        var hits = await _knowledge.RetrieveAsync(query, 5, cancellationToken);
        foreach (var hit in hits.Where(h => string.Equals(h.DocumentId, context.DocumentId, StringComparison.Ordinal)).Take(5))
            observations.Add($"[retrieve_evidence] seq={hit.Seq} :: {Snippet(hit.Content)}");

        foreach (var (chunk, evidence) in EvidenceLocator.Locate(context.Chunks, finding.Expected, 3))
            observations.Add($"[locate_evidence] seq={chunk.Seq} :: {evidence}");

        return (observations, observations.Count > 0);
    }

    private static string BuildQuery(ReviewFindingRecord finding)
        => string.Join(' ', new[] { finding.Category, finding.Title, finding.Expected }
            .Where(v => !string.IsNullOrWhiteSpace(v)));

    private static string Snippet(string content)
        => content.Length <= 120 ? content : content[..120];

    private static AdvisorConclusionDraft NoEvidence(ReviewFindingRecord finding)
        => new(finding.Id, ConclusionStatuses.Insufficient, finding.Title,
            null, "资料不足，建议人工复核。", null,
            ConclusionBasis.NoEvidence, 0, "rule", null, null, null, null, null, null, 0);

    private static ReviewConclusionRecord ToRecord(long runId, AdvisorConclusionDraft d) => new()
    {
        RunId = runId,
        FindingId = d.FindingId,
        Status = d.Status,
        Title = d.Title,
        Conclusion = d.Conclusion,
        Suggestion = d.Suggestion,
        Impact = d.Impact,
        Basis = d.Basis,
        Confidence = d.Confidence,
        Source = d.Source,
        ChunkId = d.ChunkId,
        Seq = d.Seq,
        PageFrom = d.PageFrom,
        PageTo = d.PageTo,
        Evidence = d.Evidence,
        Citations = d.Citations,
        PromptVersion = AdvisorPrompts.Version,
        StepsUsed = d.StepsUsed,
        CreatedAt = DateTimeOffset.UtcNow.ToString("o")
    };

    private static ReviewAgentStepRecord ToRecord(long runId, AdvisorStepDraft s) => new()
    {
        RunId = runId,
        FindingId = s.FindingId,
        StepIndex = s.StepIndex,
        Kind = s.Kind,
        Tool = s.Tool,
        Arguments = s.Arguments,
        Result = s.Result,
        CreatedAt = DateTimeOffset.UtcNow.ToString("o")
    };

    private async Task FailAsync(AdvisorJobRecord job, string error)
    {
        job.Status = AdvisorJobStatuses.Failed;
        job.Error = error;
        job.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
        await _repository.UpdateJobAsync(job);
    }
}
