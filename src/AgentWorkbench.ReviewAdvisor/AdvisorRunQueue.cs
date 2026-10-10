using System.Threading.Channels;

namespace AgentWorkbench.ReviewAdvisor;

/// <summary>待执行的审查结论生成任务队列。</summary>
public interface IAdvisorRunQueue
{
    ValueTask EnqueueAsync(long runId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class AdvisorRunQueue : IAdvisorRunQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ILogger<AdvisorRunQueue> _logger;

    public AdvisorRunQueue(ILogger<AdvisorRunQueue> logger) => _logger = logger;

    public ValueTask EnqueueAsync(long runId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Enqueue advisor run {RunId}", runId);
        return _channel.Writer.WriteAsync(runId, cancellationToken);
    }

    public async IAsyncEnumerable<long> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var runId in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            _logger.LogDebug("Dequeue advisor run {RunId}", runId);
            yield return runId;
        }
    }
}
