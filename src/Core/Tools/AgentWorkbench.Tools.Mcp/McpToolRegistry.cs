using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace AgentWorkbench.Tools.Mcp;

/// <summary>
/// MCP 连接与工具缓存：启动时连一遍所有启用的 server，把工具拉进内存，之后只读。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不每次调用现连：<c>IAgentToolProvider.GetTools()</c> 是同步签名，而 MCP 建连是异步的。
/// 在同步方法里阻塞等待异步建连，会把工具环拖成「每次 run 都多一次握手」，还可能死锁。
/// 所以连接放在启动期（<see cref="McpConnectionHostedService"/>），工具目录只读快照。
/// </para>
/// <para>
/// 失败隔离：一个 server 连不上只影响它自己，其余照常。这是刻意的——如果因为一个可选能力
/// 拖垮整个进程，用户会为了「能启动」去关掉所有 MCP，最后等于没接。
/// 但失败也**不会**退化成「什么都没发生」：日志里有 Error，工具目录里该 server 一个名字都没有。
/// </para>
/// </remarks>
public sealed class McpToolRegistry : IAsyncDisposable
{
    private readonly IOptionsMonitor<McpOptions> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpToolRegistry> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<McpClient> _clients = [];
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);
    private volatile IReadOnlyList<AITool> _tools = [];

    public McpToolRegistry(
        IOptionsMonitor<McpOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<McpToolRegistry> logger)
    {
        _options = options;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>当前可用工具（名字已带 <c>mcp__{server}__</c> 前缀）。连接失败时为空。</summary>
    public IReadOnlyList<AITool> Tools => _tools;

    /// <summary>工具全名 → server 名。用于轨迹与排障时指出「这一步碰的是哪个外部系统」。</summary>
    public IReadOnlyDictionary<string, string> Sources => _sources;

    /// <summary>建连并拉取工具。重复调用会先释放旧连接。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await DisposeClientsAsync();
            _sources.Clear();

            var options = _options.CurrentValue;
            if (!options.Enabled)
            {
                _tools = [];
                _logger.LogInformation("MCP 未启用（Mcp:Enabled=false），工具目录不含任何外部工具");
                return;
            }

            var collected = new List<AITool>();
            foreach (var server in options.Servers.Where(s => s.Enabled))
            {
                if (string.IsNullOrWhiteSpace(server.Name))
                {
                    _logger.LogWarning("MCP server 缺少 Name，已跳过：Transport={Transport}", server.Transport);
                    continue;
                }

                try
                {
                    await ConnectAndCollectAsync(server, collected, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MCP server 连接失败：{Server}（{Transport}）", server.Name, server.Transport);
                }
            }

            _tools = collected;
            _logger.LogInformation("MCP 工具目录：{Count} 个工具，来自 {Servers} 个 server",
                collected.Count, _sources.Values.Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ConnectAndCollectAsync(
        McpServerOptions server,
        List<AITool> collected,
        CancellationToken cancellationToken)
    {
        var client = await McpClient.CreateAsync(
            CreateTransport(server),
            loggerFactory: _loggerFactory,
            cancellationToken: cancellationToken);

        _clients.Add(client);

        var listed = await client.ListToolsAsync(cancellationToken: cancellationToken);
        var allow = server.AllowTools;

        var added = 0;
        foreach (var tool in listed)
        {
            if (allow.Count > 0 && !allow.Contains(tool.Name, StringComparer.Ordinal))
                continue;

            var fullName = McpOptions.ToolNamePrefix + server.Name + "__" + tool.Name;
            if (_sources.ContainsKey(fullName))
            {
                // 同名工具只会静默覆盖，而 ToolCatalog 的去重会让后一个彻底消失。
                // 与其让它悄悄不见，不如在这里说清楚是谁被丢了。
                _logger.LogWarning("MCP 工具名冲突，已跳过：{Tool}（server {Server}）", fullName, server.Name);
                continue;
            }

            _sources[fullName] = server.Name;
            collected.Add(tool.WithName(fullName));
            added++;
        }

        _logger.LogInformation("MCP server {Server} 连上，暴露 {Added}/{Total} 个工具",
            server.Name, added, listed.Count);
    }

    private static IClientTransport CreateTransport(McpServerOptions server)
    {
        var kind = server.Transport.Trim().ToLowerInvariant();
        switch (kind)
        {
            case "stdio":
                if (string.IsNullOrWhiteSpace(server.Command))
                    throw new InvalidOperationException($"MCP server {server.Name} 是 stdio，但没配 Command");
                return new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = server.Name,
                    Command = server.Command,
                    Arguments = server.Arguments,
                    EnvironmentVariables = server.Environment.Count > 0 ? server.Environment : null,
                    InheritEnvironmentVariables = server.InheritEnvironment,
                });

            case "http":
                if (string.IsNullOrWhiteSpace(server.Endpoint))
                    throw new InvalidOperationException($"MCP server {server.Name} 是 http，但没配 Endpoint");
                return new HttpClientTransport(new HttpClientTransportOptions
                {
                    Name = server.Name,
                    Endpoint = new Uri(server.Endpoint, UriKind.Absolute),
                    AdditionalHeaders = server.Headers.Count > 0
                        ? server.Headers.ToDictionary(kv => kv.Key, kv => (string?)kv.Value)
                        : null,
                });

            default:
                throw new InvalidOperationException(
                    $"MCP server {server.Name} 的 Transport 只能是 stdio 或 http，当前是「{server.Transport}」");
        }
    }

    private async Task DisposeClientsAsync()
    {
        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "释放 MCP 连接时出错");
            }
        }
        _clients.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DisposeClientsAsync();
            _tools = [];
            _sources.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
