using System.Threading.Channels;

namespace AgentWorkbench.Review;

/// <summary>待执行审查任务的内部队列。</summary>
public interface IReviewRunQueue
{
    ValueTask EnqueueAsync(long runId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class ReviewRunQueue : IReviewRunQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ILogger<ReviewRunQueue> _logger;

    public ReviewRunQueue(ILogger<ReviewRunQueue> logger) => _logger = logger;

    public ValueTask EnqueueAsync(long runId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Enqueue review run {RunId}", runId);
        return _channel.Writer.WriteAsync(runId, cancellationToken);
    }

    public async IAsyncEnumerable<long> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var runId in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            _logger.LogDebug("Dequeue review run {RunId}", runId);
            yield return runId;
        }
    }
}
