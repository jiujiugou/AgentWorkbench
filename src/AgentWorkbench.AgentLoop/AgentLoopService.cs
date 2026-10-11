using Microsoft.Extensions.Options;

namespace AgentWorkbench.AgentLoop;

/// <inheritdoc />
public sealed class AgentLoopService : IAgentLoop
{
    private readonly AgentLoopOptions _options;
    private readonly ILogger<AgentLoopService> _logger;

    public AgentLoopService(IOptions<AgentLoopOptions> options, ILogger<AgentLoopService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public int MaxIterations => _options.MaxIterations;

    public int TimeoutSeconds => _options.TimeoutSeconds;

    public async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            return await action(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Agent run 超时：{TimeoutSeconds}s（MaxIterations={MaxIterations}）",
                _options.TimeoutSeconds, _options.MaxIterations);
            throw new AgentRunTimeoutException(_options.TimeoutSeconds);
        }
    }
}
