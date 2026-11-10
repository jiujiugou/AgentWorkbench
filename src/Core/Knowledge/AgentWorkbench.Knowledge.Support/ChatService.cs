using AgentWorkbench.Runtime.Model;

namespace AgentWorkbench.Knowledge.Support;

/// <inheritdoc />
public sealed class ChatService : IChatService
{
    private readonly IChatClientFactory _factory;
    private readonly ILogger<ChatService> _logger;

    public ChatService(IChatClientFactory factory, ILogger<ChatService> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        var configured = (await _factory.ResolveAsync(null, cancellationToken)) is not null;
        _logger.LogDebug("IsConfiguredAsync: {Configured}", configured);
        return configured;
    }
}
