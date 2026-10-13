using Microsoft.Extensions.Options;

namespace AgentWorkbench.Safety;

/// <summary>工具授权策略：决定某个工具名是否被允许。</summary>
public interface IToolPolicy
{
    /// <summary>白名单快照。</summary>
    IReadOnlyList<string> Whitelist { get; }

    bool IsAllowed(string toolName);

    IReadOnlyList<string> Allowed(IEnumerable<string> toolNames);
}

/// <inheritdoc />
/// <remarks>
/// 持 <see cref="IOptionsMonitor{T}"/> 而非 <see cref="IOptions{T}"/>：白名单住进 appsettings.json 后，
/// 修改保存即热重载（JSON provider 默认 reloadOnChange），下个 run 生效——工具「上线/下线」零停服。
/// 每次访问都取 <c>CurrentValue</c>，不缓存快照；ToolCatalog 亦是每次调用现解析，链路两端都不做静态假设。
/// </remarks>
public sealed class ToolPolicy : IToolPolicy
{
    private readonly IOptionsMonitor<ToolPolicyOptions> _options;

    public ToolPolicy(IOptionsMonitor<ToolPolicyOptions> options) => _options = options;

    public IReadOnlyList<string> Whitelist => _options.CurrentValue.Whitelist;

    public bool IsAllowed(string toolName)
        => _options.CurrentValue.Whitelist.Contains(toolName, StringComparer.Ordinal);

    public IReadOnlyList<string> Allowed(IEnumerable<string> toolNames)
        => toolNames.Where(IsAllowed).ToList();
}
