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
public sealed class ToolPolicy : IToolPolicy
{
    private readonly ToolPolicyOptions _options;

    public ToolPolicy(IOptions<ToolPolicyOptions> options) => _options = options.Value;

    public IReadOnlyList<string> Whitelist => _options.Whitelist;

    public bool IsAllowed(string toolName)
        => _options.Whitelist.Contains(toolName, StringComparer.Ordinal);

    public IReadOnlyList<string> Allowed(IEnumerable<string> toolNames)
        => toolNames.Where(IsAllowed).ToList();
}
