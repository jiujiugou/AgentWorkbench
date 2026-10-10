namespace AgentWorkbench.ReviewAdvisor;

/// <summary>后台单读者：出队 runId → 执行审查结论生成（Agent Loop）。</summary>
public sealed class AdvisorWorker : BackgroundService
{
    private readonly IAdvisorRunQueue _queue;
    private readonly IReviewAdvisorService _service;
    private readonly ILogger<AdvisorWorker> _logger;

    public AdvisorWorker(IAdvisorRunQueue queue, IReviewAdvisorService service, ILogger<AdvisorWorker> logger)
    {
        _queue = queue;
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AdvisorWorker started.");
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
                _logger.LogError(ex, "审查结论生成失败 {RunId}", runId);
            }
        }
        _logger.LogInformation("AdvisorWorker stopped.");
    }
}
