using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Memory.Persistence;

/// <summary>
/// 定期删除过期的会话行。读路径也会判过期，但没有后台清理时失效行会一直留在表里。
/// </summary>
public sealed class SessionRetentionService : BackgroundService
{
    private readonly ISessionStateRepository _repository;
    private readonly MemoryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionRetentionService> _logger;

    public SessionRetentionService(
        ISessionStateRepository repository,
        IOptions<MemoryOptions> options,
        TimeProvider timeProvider,
        ILogger<SessionRetentionService> logger)
    {
        _repository = repository;
        _options = options.Value;
        _time = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // TTL 关掉时不会写 expires_at，没有可清理的行。
        if (_options.SessionTtlMinutes <= 0)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.EvictionSweepSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = _time.GetUtcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                var removed = await _repository.DeleteExpiredAsync(now, stoppingToken);
                if (removed > 0)
                    _logger.LogInformation("清理过期会话 {Count} 条", removed);
            }
        }
        catch (OperationCanceledException)
        {
            // 停机，正常退出。
        }
    }
}
