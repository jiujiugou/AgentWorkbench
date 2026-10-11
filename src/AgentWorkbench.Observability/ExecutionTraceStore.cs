namespace AgentWorkbench.Observability;

/// <inheritdoc />
public sealed class ExecutionTraceStore : IExecutionTraceStore
{
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
        return new ExecutionTrace(
            run.SessionId,
            run.Status,
            run.Iterations,
            run.Model,
            run.LatencyMs ?? 0,
            run.Error,
            steps.Select(s => new ExecutionStep(s.Iteration, s.Kind, s.Tool, s.Arguments, s.Result, s.Error)).ToList());
    }
}
