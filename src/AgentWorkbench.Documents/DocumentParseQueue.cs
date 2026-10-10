using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace AgentWorkbench.Documents;

/// <summary>待解析文档的内部队列。</summary>
public interface IDocumentParseQueue
{
    ValueTask EnqueueAsync(string documentId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class DocumentParseQueue : IDocumentParseQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ILogger<DocumentParseQueue> _logger;

    public DocumentParseQueue(ILogger<DocumentParseQueue> logger)
    {
        _logger = logger;
    }

    public ValueTask EnqueueAsync(string documentId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Enqueue document {DocumentId}", documentId);
        return _channel.Writer.WriteAsync(documentId, cancellationToken);
    }

    public async IAsyncEnumerable<string> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("DocumentParseQueue reader started");
        await foreach (var id in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            _logger.LogDebug("Dequeue document {DocumentId}", id);
            yield return id;
        }
        _logger.LogInformation("DocumentParseQueue reader stopped");
    }
}
