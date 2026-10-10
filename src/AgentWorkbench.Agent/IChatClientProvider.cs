using AgentWorkbench.Providers;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Agent;

/// <summary>
/// 提供按当前激活供应商/模型解析出的 <see cref="IChatClient"/>。
/// 供需要原生函数调用（function calling）的模块复用同一模型网关。
/// </summary>
public interface IChatClientProvider
{
    Task<IChatClient?> GetClientAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ChatClientProvider : IChatClientProvider
{
    private readonly IChatClientFactory _factory;

    public ChatClientProvider(IChatClientFactory factory) => _factory = factory;

    public async Task<IChatClient?> GetClientAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = await _factory.ResolveAsync(null, cancellationToken);
        return endpoint is null ? null : _factory.GetClient(endpoint);
    }
}
