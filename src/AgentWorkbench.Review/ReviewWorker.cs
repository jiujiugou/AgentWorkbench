namespace AgentWorkbench.Review;

/// <summary>后台单读者：出队 runId → 执行审查（要素抽取 + 比对）。</summary>
public sealed class ReviewWorker : BackgroundService
{
    private readonly IReviewRunQueue _queue;
    private readonly IReviewService _service;
    private readonly ILogger<ReviewWorker> _logger;

    public ReviewWorker(IReviewRunQueue queue, IReviewService service, ILogger<ReviewWorker> logger)
    {
        _queue = queue;
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReviewWorker started.");

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
                _logger.LogError(ex, "审查任务执行失败 {RunId}", runId);
            }
        }

        _logger.LogInformation("ReviewWorker stopped.");
    }
}
