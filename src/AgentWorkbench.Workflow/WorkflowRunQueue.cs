using System.Threading.Channels;

namespace AgentWorkbench.Workflow;

/// <summary>待执行的工作流运行队列。</summary>
public interface IWorkflowRunQueue
{
    ValueTask EnqueueAsync(long runId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<long> ReadAllAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class WorkflowRunQueue : IWorkflowRunQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ValueTask EnqueueAsync(long runId, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(runId, cancellationToken);

    public async IAsyncEnumerable<long> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var runId in _channel.Reader.ReadAllAsync(cancellationToken))
            yield return runId;
    }
}
