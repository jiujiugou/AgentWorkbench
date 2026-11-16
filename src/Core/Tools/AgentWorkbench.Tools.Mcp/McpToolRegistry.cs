using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace AgentWorkbench.Tools.Mcp;

/// <summary>一个 MCP 工具的能力画像：从 server 声明的标注里读出来，读不到就是「不知道」。</summary>
/// <remarks>
/// 为什么要单独存这一份：<see cref="AITool"/> 只保留名字与 schema，MCP 的
/// <c>ReadOnlyHint</c> / <c>DestructiveHint</c> / <c>IdempotentHint</c> 在 <c>WithName</c> 之后
/// 就没有任何下游能读到。丢了这份标注，整条链路就无法回答「这一步会不会改别人的数据」。
/// <para>
/// 三个字段都是 <c>bool?</c>，因为「未声明」和「声明为 false」是两件事：
/// 未声明 = server 没告诉我们 = 不可信；声明 false = 它明确说了「不保证只读」。
/// 两者在准入上同归「不进模型目录」，但在排障时必须能区分。
/// </para>
/// </remarks>
public sealed record McpToolCapability(
    string FullName,
    string Server,
    string ToolName,
    bool? ReadOnly,
    bool? Destructive,
    bool? Idempotent,
    bool CatalogEligible)
{
    /// <summary>从 SDK 的工具对象与 server 配置里算出这份画像。</summary>
    /// <remarks>
    /// 准入按三条从强到弱：
    /// <list type="number">
    /// <item>声明了 <c>DestructiveHint=true</c> → 永远不进模型目录，信任名单也翻不了这条；</item>
    /// <item>声明了 <c>ReadOnlyHint=true</c> → 可以进（只读工具直通，这是检索那条既有链路）；</item>
    /// <item>什么都没声明 → 按「会写」处理，只有被 <c>TrustedReadOnly</c> 逐个点名才进。</item>
    /// </list>
    /// 换句话说：<b>默认是关的</b>。要开，要么 server 自己声明只读，要么我们写下它的名字。
    /// </remarks>
    public static McpToolCapability From(McpServerOptions server, AITool tool, string fullName)
    {
        var annotations = (tool as McpClientTool)?.ProtocolTool.Annotations;
        var readOnly = annotations?.ReadOnlyHint;
        var destructive = annotations?.DestructiveHint;
        var idempotent = annotations?.IdempotentHint;

        var trusted = server.TrustedReadOnly.Contains(tool.Name, StringComparer.Ordinal);
        var eligible = destructive != true && (readOnly == true || trusted);

        return new McpToolCapability(fullName, server.Name, tool.Name, readOnly, destructive, idempotent, eligible);
    }

    /// <summary>排障用的一行：把人需要看到的四个事实压成一行。</summary>
    public string Describe()
        => $"{ToolName} readOnly={Flag(ReadOnly)} destructive={Flag(Destructive)} "
           + $"idempotent={Flag(Idempotent)} → {(CatalogEligible ? "进模型目录" : "仅可由动作闸调用")}";

    private static string Flag(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        null => "未声明",
    };
}

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
/// <para>
/// <b>准入在这里做，不在下游做。</b> 一个工具能不能被模型直接看见，由它的能力画像决定
/// （见 <see cref="McpToolCapability.From"/>）；不能看见的那部分不会被丢弃——它们仍留在
/// <see cref="Tools"/> 里，供动作闸背后的执行体按名字调用。
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
    private readonly Dictionary<string, McpToolCapability> _capabilities = new(StringComparer.Ordinal);
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

    /// <summary>全部可用工具（名字已带 <c>mcp__{server}__</c> 前缀），含不能进模型目录的那些。</summary>
    public IReadOnlyList<AITool> Tools => _tools;

    /// <summary>工具全名 → 能力画像。准入、审计、排障都读这一份。</summary>
    public IReadOnlyDictionary<string, McpToolCapability> Capabilities => _capabilities;

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
            _capabilities.Clear();

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
        var eligible = 0;
        var lines = new List<string>();

        foreach (var tool in listed)
        {
            // AllowTools 留空 = 一个都不放行。
            // 默认必须是空集：「没想清楚」不能等于「全都给」。
            if (!allow.Contains(tool.Name, StringComparer.Ordinal))
                continue;

            var fullName = McpOptions.ToolNamePrefix + server.Name + "__" + tool.Name;
            if (_sources.ContainsKey(fullName))
            {
                // 同名工具只会静默覆盖，而 ToolCatalog 的去重会让后一个彻底消失。
                // 与其让它悄悄不见，不如在这里说清楚是谁被丢了。
                _logger.LogWarning("MCP 工具名冲突，已跳过：{Tool}（server {Server}）", fullName, server.Name);
                continue;
            }

            var capability = McpToolCapability.From(server, tool, fullName);
            _sources[fullName] = server.Name;
            _capabilities[fullName] = capability;
            collected.Add(tool.WithName(fullName));
            added++;
            if (capability.CatalogEligible) eligible++;
            lines.Add(capability.Describe());
        }

        // 把明细打出来不是为了好看：AllowTools 写错一个字母、或者 server 没声明 readOnly，
        // 后果都是「静默少一个工具」——不报错、不告警，只在某天模型说「我没有这个能力」时才被发现。
        // 一行一个工具的准入结论，是唯一低成本的排障手段。
        _logger.LogInformation(
            "MCP server {Server} 连上：放行 {Added}/{Total} 个，其中可进模型目录 {Eligible} 个。明细：{Detail}",
            server.Name, added, listed.Count, eligible,
            lines.Count == 0 ? "（无）" : string.Join(" | ", lines));
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
            _capabilities.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
