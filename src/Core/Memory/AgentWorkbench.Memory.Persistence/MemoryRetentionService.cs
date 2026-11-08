using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Memory.Persistence;

/// <summary>
/// 定期回收过期数据：过期会话行 + 过保留期的记忆行。
/// 读路径也会判过期，但没有后台清理时失效行会一直留在表里。
/// </summary>
public sealed class MemoryRetentionService : BackgroundService
{
    private readonly ISessionStateRepository _sessions;
    private readonly ILongTermMemoryStore _memory;
    private readonly MemoryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<MemoryRetentionService> _logger;

    public MemoryRetentionService(
        ISessionStateRepository sessions,
        ILongTermMemoryStore memory,
        IOptions<MemoryOptions> options,
        TimeProvider timeProvider,
        ILogger<MemoryRetentionService> logger)
    {
        _sessions = sessions;
        _memory = memory;
        _options = options.Value;
        _time = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.EvictionSweepSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SweepOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 停机，正常退出。
        }
    }

    /// <summary>跑一轮清理（会话 + 记忆）。抽出来是为了让判定型用例不必等后台定时器。</summary>
    public async Task SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        // TTL 关掉时不会写 expires_at，没有可清理的会话行。
        if (_options.SessionTtlMinutes > 0)
        {
            var now = Iso();
            await ReportAsync("过期会话", cancellationToken,
                () => _sessions.DeleteExpiredAsync(now, cancellationToken));
        }

        // 记忆的保留期按条记录（expires_at），与全局 TTL 开关无关，照清不误。
        await ReportAsync("过期记忆", cancellationToken, () => _memory.PurgeExpiredAsync(cancellationToken));
    }

    private async Task ReportAsync(string label, CancellationToken stoppingToken, Func<Task<int>> sweep)
    {
        try
        {
            var removed = await sweep();
            if (removed > 0)
                _logger.LogInformation("清理{Label} {Count} 条", label, removed);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 一轮清理失败不能让后台循环退出（否则后面的清理就再也不会发生了）。
            _logger.LogWarning(ex, "清理{Label}失败", label);
        }
    }

    private string Iso()
        => _time.GetUtcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
