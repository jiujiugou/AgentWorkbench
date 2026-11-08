namespace AgentWorkbench.Workflow;

/// <summary>后台单读者：出队 runId → 执行工作流。</summary>
public sealed class WorkflowWorker : BackgroundService
{
    private readonly IWorkflowRunQueue _queue;
    private readonly IWorkflowService _service;
    private readonly ILogger<WorkflowWorker> _logger;

    public WorkflowWorker(IWorkflowRunQueue queue, IWorkflowService service, ILogger<WorkflowWorker> logger)
    {
        _queue = queue;
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WorkflowWorker started.");
        await foreach (var runId in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _service.ExecuteAsync(runId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工作流执行失败 {RunId}", runId);
            }
        }
        _logger.LogInformation("WorkflowWorker stopped.");
    }
}
