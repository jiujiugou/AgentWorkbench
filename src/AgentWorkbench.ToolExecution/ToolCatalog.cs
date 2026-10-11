namespace AgentWorkbench.ToolExecution;

/// <summary>
/// 工具目录：汇总所有 <see cref="IAgentToolProvider"/> 提供的工具，并按 <see cref="IToolPolicy"/> 白名单过滤。
/// 对话（Conversation）与编排（AgentHarness）共用同一份「可用工具」视图，避免两边白名单不一致。
/// </summary>
public interface IToolCatalog
{
    /// <summary>当前允许注册的工具（已按白名单过滤、按名称去重）。</summary>
    IReadOnlyList<AITool> GetTools();

    /// <summary>当前允许的工具名。</summary>
    IReadOnlyList<string> GetToolNames();
}

/// <inheritdoc />
public sealed class ToolCatalog : IToolCatalog
{
    private readonly IServiceProvider _services;
    private readonly IToolPolicy _policy;
    private readonly ILogger<ToolCatalog> _logger;

    public ToolCatalog(IServiceProvider services, IToolPolicy policy, ILogger<ToolCatalog> logger)
    {
        _services = services;
        _policy = policy;
        _logger = logger;
    }

    public IReadOnlyList<AITool> GetTools()
    {
        // 延迟解析：工具提供方内部可能依赖知识库/文档仓储，构造期解析会形成 DI 环。
        var available = _services.GetRequiredService<IEnumerable<IAgentToolProvider>>()
            .SelectMany(provider => provider.GetTools())
            .ToList();

        var allowed = available
            .Where(tool => _policy.IsAllowed(tool.Name))
            .DistinctBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();

        if (allowed.Count < available.Count)
        {
            _logger.LogDebug("工具目录按白名单裁剪：可用 {Available} → 允许 {Allowed}", available.Count, allowed.Count);
        }

        return allowed;
    }

    public IReadOnlyList<string> GetToolNames()
        => GetTools().Select(tool => tool.Name).ToList();
}
