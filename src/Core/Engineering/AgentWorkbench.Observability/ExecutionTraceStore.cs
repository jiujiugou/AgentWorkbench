namespace AgentWorkbench.Observability;

/// <inheritdoc />
public sealed class ExecutionTraceStore : IExecutionTraceStore
{
    /// <summary>未指定或非正数时的返回条数上限。</summary>
    private const int DefaultLimit = 50;

    private readonly IAgentRunRepository _repository;
    private readonly ILogger<ExecutionTraceStore> _logger;

    public ExecutionTraceStore(IAgentRunRepository repository, ILogger<ExecutionTraceStore> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<long> SaveAsync(ExecutionTrace trace, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        var runId = await _repository.InsertRunAsync(new AgentRunRecord
        {
            SessionId = trace.SessionId,
            Status = trace.Status,
            Iterations = trace.Iterations,
            Model = trace.Model,
            LatencyMs = trace.LatencyMs,
            Error = trace.Error,
            ErrorCode = trace.ErrorCode,
            CreatedAt = now,
            FinishedAt = now
        });

        await _repository.InsertStepsAsync(runId, trace.Steps.Select(step => new AgentStepRecord
        {
            Iteration = step.Iteration,
            Kind = step.Kind,
            Tool = step.Tool,
            Arguments = step.Arguments,
            Result = step.Result,
            Error = step.Error,
            CreatedAt = now
        }).ToList());

        _logger.LogInformation(
            "Recorded execution trace {RunId} session={SessionId} status={Status} iterations={Iterations}",
            runId, trace.SessionId, trace.Status, trace.Iterations);
        return runId;
    }

    public async Task<ExecutionTrace?> GetAsync(long runId, CancellationToken cancellationToken = default)
    {
        var run = await _repository.GetRunAsync(runId);
        if (run is null)
            return null;

        var steps = await _repository.GetStepsAsync(runId);
        return Map(run, steps);
    }

    public async Task<IReadOnlyList<ExecutionTrace>> QueryAsync(
        ExecutionTraceQuery query, CancellationToken cancellationToken = default)
    {
        var limit = query.Limit > 0 ? query.Limit : DefaultLimit;
        var runs = await _repository.QueryRunsAsync(query.SessionId, query.Status, limit);

        var traces = new List<ExecutionTrace>(runs.Count);
        foreach (var run in runs)
        {
            // 逐条补步骤：一次检索最多 Limit 条，属于排障 / 回放路径，不做批量联表优化。
            var steps = await _repository.GetStepsAsync(run.Id);
            traces.Add(Map(run, steps));
        }

        return traces;
    }

    /// <summary>运行行 + 步骤行 → 轨迹；<see cref="GetAsync"/> 与 <see cref="QueryAsync"/> 共用，避免两处映射漂移。</summary>
    private static ExecutionTrace Map(AgentRunRecord run, IReadOnlyList<AgentStepRecord> steps)
        => new(
            run.SessionId,
            run.Status,
            run.Iterations,
            run.Model,
            run.LatencyMs ?? 0,
            run.Error,
            run.ErrorCode,
            steps.Select(s => new ExecutionStep(s.Iteration, s.Kind, s.Tool, s.Arguments, s.Result, s.Error)).ToList());
}
