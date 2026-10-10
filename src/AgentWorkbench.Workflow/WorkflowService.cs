using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AgentWorkbench.Workflow.Execution;
using AgentWorkbench.Workflow.Prompting;
using AgentWorkbench.Workflow.Steps;
using Microsoft.Agents.AI.Workflows;

namespace AgentWorkbench.Workflow;

/// <summary>P4 工作流服务：创建 / 查询 / 取消 / 单步重跑 / 断点续跑 / 执行。</summary>
public interface IWorkflowService
{
    Task<WorkflowCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowDefinitionRecord>> ListDefinitionsAsync(CancellationToken cancellationToken = default);

    Task<long> CreateRunAsync(string documentId, string? workflowKey, string? promptVersion, CancellationToken cancellationToken = default);

    Task<WorkflowRunView?> GetRunAsync(long runId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkflowRunView>> ListRunsAsync(string? documentId, CancellationToken cancellationToken = default);

    Task<bool> CancelAsync(long runId, CancellationToken cancellationToken = default);

    Task RerunStepAsync(long runId, string stepKey, CancellationToken cancellationToken = default);

    Task ResumeAsync(long runId, CancellationToken cancellationToken = default);

    Task<bool> DeleteRunAsync(long runId, CancellationToken cancellationToken = default);

    Task ExecuteAsync(long runId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class WorkflowService : IWorkflowService
{
    public const string DefaultWorkflowKey = "contract_review";

    private readonly IWorkflowRepository _repository;
    private readonly IDocumentRepository _documents;
    private readonly WorkflowStepRegistry _steps;
    private readonly IPromptStore _prompts;
    private readonly IWorkflowRunQueue _queue;
    private readonly WorkflowGraphBuilder _graphs;
    private readonly ILogger<WorkflowService> _logger;

    private readonly ConcurrentDictionary<long, CancellationTokenSource> _cancellations = new();

    public WorkflowService(
        IWorkflowRepository repository,
        IDocumentRepository documents,
        WorkflowStepRegistry steps,
        IPromptStore prompts,
        IWorkflowRunQueue queue,
        WorkflowGraphBuilder graphs,
        ILogger<WorkflowService> logger)
    {
        _repository = repository;
        _documents = documents;
        _steps = steps;
        _prompts = prompts;
        _queue = queue;
        _graphs = graphs;
        _logger = logger;
    }

    public Task<WorkflowCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new WorkflowCapabilities(true, _steps.Types));

    public Task<IReadOnlyList<WorkflowDefinitionRecord>> ListDefinitionsAsync(CancellationToken cancellationToken = default)
        => _repository.ListDefinitionsAsync();

    public async Task<long> CreateRunAsync(string documentId, string? workflowKey, string? promptVersion, CancellationToken cancellationToken = default)
    {
        var document = await _documents.GetAsync(documentId)
            ?? throw new WorkflowDocumentNotFoundException(documentId);
        if (!string.Equals(document.Status, DocumentStatus.Parsed, StringComparison.Ordinal))
            throw new WorkflowDocumentNotReadyException(documentId, document.Status);

        var key = string.IsNullOrWhiteSpace(workflowKey) ? DefaultWorkflowKey : workflowKey!;
        var definition = await LoadDefinitionAsync(key)
            ?? throw new WorkflowDefinitionNotFoundException(key);

        if (!string.IsNullOrWhiteSpace(promptVersion))
        {
            foreach (var step in definition.Steps.Where(s => !string.IsNullOrWhiteSpace(s.PromptKey)))
            {
                if (!await _prompts.ExistsAsync(step.PromptKey!, promptVersion!, cancellationToken))
                    throw new WorkflowPromptNotFoundException(step.PromptKey!, promptVersion!);
            }
        }

        var run = await _repository.CreateRunAsync(new WorkflowRunRecord
        {
            WorkflowKey = definition.Key,
            DocumentId = documentId,
            PromptVersion = promptVersion,
            Status = WorkflowRunStatuses.Pending
        });

        var steps = definition.Steps
            .OrderBy(s => s.Order)
            .Select(s => new WorkflowStepRecord
            {
                RunId = run.Id,
                StepKey = s.Key,
                StepType = s.Type,
                OrderIndex = s.Order,
                Status = WorkflowStepStatuses.Pending,
                Attempts = 0
            })
            .ToList();
        await _repository.CreateStepsAsync(run.Id, steps);

        await _queue.EnqueueAsync(run.Id, cancellationToken);
        _logger.LogInformation("Created workflow run {RunId} ({Key}) for document {DocumentId}", run.Id, definition.Key, documentId);
        return run.Id;
    }

    public async Task<WorkflowRunView?> GetRunAsync(long runId, CancellationToken cancellationToken = default)
    {
        var run = await _repository.GetRunAsync(runId);
        if (run is null)
            return null;
        var steps = await _repository.GetStepsAsync(runId);
        return ToView(run, steps);
    }

    public async Task<IReadOnlyList<WorkflowRunView>> ListRunsAsync(string? documentId, CancellationToken cancellationToken = default)
    {
        var runs = await _repository.ListRunsAsync(documentId);
        var views = new List<WorkflowRunView>(runs.Count);
        foreach (var run in runs)
        {
            var steps = await _repository.GetStepsAsync(run.Id);
            views.Add(ToView(run, steps));
        }
        return views;
    }

    public async Task<bool> CancelAsync(long runId, CancellationToken cancellationToken = default)
    {
        if (_cancellations.TryGetValue(runId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        var run = await _repository.GetRunAsync(runId);
        if (run is null)
            return false;
        if (run.Status is WorkflowRunStatuses.Pending or WorkflowRunStatuses.Running)
        {
            run.Status = WorkflowRunStatuses.Cancelled;
            run.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
            await _repository.UpdateRunAsync(run);
            return true;
        }
        return false;
    }

    public async Task RerunStepAsync(long runId, string stepKey, CancellationToken cancellationToken = default)
    {
        var run = await _repository.GetRunAsync(runId)
            ?? throw new WorkflowRunNotFoundException(runId);
        var steps = await _repository.GetStepsAsync(runId);
        var target = steps.FirstOrDefault(s => string.Equals(s.StepKey, stepKey, StringComparison.Ordinal))
            ?? throw new WorkflowStepNotFoundException(runId, stepKey);

        await _repository.ResetStepAsync(target.Id);
        await _repository.MarkDownstreamStaleAsync(runId, target.OrderIndex);

        run.Status = WorkflowRunStatuses.Pending;
        run.Error = null;
        run.FinishedAt = null;
        run.LatencyMs = null;
        await _repository.UpdateRunAsync(run);
        await _queue.EnqueueAsync(runId, cancellationToken);
        _logger.LogInformation("Rerun step {StepKey} for run {RunId} (downstream marked stale)", stepKey, runId);
    }

    public async Task ResumeAsync(long runId, CancellationToken cancellationToken = default)
    {
        var run = await _repository.GetRunAsync(runId)
            ?? throw new WorkflowRunNotFoundException(runId);
        run.Status = WorkflowRunStatuses.Pending;
        run.Error = null;
        run.FinishedAt = null;
        await _repository.UpdateRunAsync(run);
        await _queue.EnqueueAsync(runId, cancellationToken);
    }

    public Task<bool> DeleteRunAsync(long runId, CancellationToken cancellationToken = default)
        => _repository.DeleteRunAsync(runId);

    public async Task ExecuteAsync(long runId, CancellationToken cancellationToken = default)
    {
        var run = await _repository.GetRunAsync(runId);
        if (run is null)
            return;
        if (run.Status is WorkflowRunStatuses.Cancelled)
            return;

        var definition = await LoadDefinitionAsync(run.WorkflowKey);
        if (definition is null)
        {
            await FailRunAsync(run, $"工作流定义不存在：{run.WorkflowKey}", 0, cancellationToken);
            return;
        }

        var document = await _documents.GetAsync(run.DocumentId);
        if (document is null)
        {
            await FailRunAsync(run, "文档不存在", 0, cancellationToken);
            return;
        }

        var chunks = await _documents.GetChunksAsync(run.DocumentId);
        var steps = (await _repository.GetStepsAsync(runId)).ToList();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellations[runId] = linked;
        var stopwatch = Stopwatch.StartNew();

        run.Status = WorkflowRunStatuses.Running;
        run.Error = null;
        await _repository.UpdateRunAsync(run);

        try
        {
            var message = new WorkflowStepMessage
            {
                Run = run,
                DocumentId = document.Id,
                FileName = document.FileName,
                Chunks = chunks,
                Steps = steps
            };

            try
            {
                var workflow = _graphs.Build(definition);
                var result = await InProcessExecution.RunAsync(
                    workflow, message, sessionId: runId.ToString(), cancellationToken: linked.Token);
                await result.DisposeAsync();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // 取消：步骤节点已落 cancelled，这里统一落 run 终态
            }
            catch (Exception ex)
            {
                await FailRunAsync(run, ex.Message, stopwatch.ElapsedMilliseconds, CancellationToken.None);
                _logger.LogError(ex, "Workflow run {RunId} execution error", runId);
                return;
            }

            if (linked.IsCancellationRequested)
            {
                run.Status = WorkflowRunStatuses.Cancelled;
                run.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
                run.LatencyMs = stopwatch.ElapsedMilliseconds;
                await _repository.UpdateRunAsync(run);
                _logger.LogWarning("Workflow run {RunId} cancelled", runId);
                return;
            }

            var failed = steps.FirstOrDefault(s =>
                string.Equals(s.Status, WorkflowStepStatuses.Failed, StringComparison.Ordinal));
            if (failed is not null)
            {
                await FailRunAsync(
                    run, failed.Error ?? "工作流执行失败", stopwatch.ElapsedMilliseconds, CancellationToken.None);
                _logger.LogError("Workflow run {RunId} failed at step {StepKey}: {Error}", runId, failed.StepKey, failed.Error);
                return;
            }

            run.Status = WorkflowRunStatuses.Succeeded;
            run.Error = null;
            run.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
            run.LatencyMs = stopwatch.ElapsedMilliseconds;
            await _repository.UpdateRunAsync(run);
            _logger.LogInformation("Workflow run {RunId} succeeded in {ElapsedMs}ms", runId, stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            _cancellations.TryRemove(runId, out _);
        }
    }

    private async Task FailRunAsync(WorkflowRunRecord run, string error, long elapsedMs, CancellationToken cancellationToken)
    {
        run.Status = WorkflowRunStatuses.Failed;
        run.Error = error;
        run.FinishedAt = DateTimeOffset.UtcNow.ToString("o");
        run.LatencyMs = elapsedMs;
        await _repository.UpdateRunAsync(run);
    }

    private async Task<WorkflowDefinition?> LoadDefinitionAsync(string key)
    {
        var record = await _repository.GetDefinitionAsync(key);
        if (record is null)
            return null;
        var document = JsonSerializer.Deserialize<WorkflowDefinitionDocument>(record.Definition) ?? new();
        return new WorkflowDefinition(record.Key, record.Name, document.Steps.OrderBy(s => s.Order).ToList());
    }

    private static WorkflowRunView ToView(WorkflowRunRecord run, IReadOnlyList<WorkflowStepRecord> steps)
        => new(
            run.Id, run.WorkflowKey, run.DocumentId, run.PromptVersion, run.Status, run.Error, run.LatencyMs,
            run.CreatedAt, run.FinishedAt,
            steps.Select(s => new WorkflowStepView(
                s.Id, s.StepKey, s.StepType, s.OrderIndex, s.Status, s.Attempts, s.Output, s.Error,
                s.StartedAt, s.FinishedAt)).ToList());
}
