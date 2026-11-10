using Microsoft.Extensions.Options;

namespace AgentWorkbench.Runtime.Tools;

/// <summary>工具授权策略：决定某个工具名是否被允许。</summary>
public interface IToolPolicy
{
    /// <summary>白名单快照。</summary>
    IReadOnlyList<string> Whitelist { get; }

    /// <summary>
    /// 精确名匹配；规则以 <c>*</c> 结尾时按前缀匹配（<c>mcp__erpnext__*</c> 放行该 server 的全部工具）。
    /// 空名一律不放行。
    /// </summary>
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
    {
        if (string.IsNullOrEmpty(toolName)) return false;

        foreach (var rule in _options.CurrentValue.Whitelist)
        {
            if (string.IsNullOrWhiteSpace(rule)) continue;

            // 只支持「尾部 *」这一种通配：mcp__erpnext__* 放行该 server 的全部工具。
            //
            // 不支持中间通配、正则、否定式——白名单是安全边界，必须能被人一眼读完并逐个签字。
            // 一旦允许写 `*document*` 这类模糊规则，读配置的人就无法回答「到底放行了哪些工具」，
            // 而回答不了这个问题的白名单等于没有白名单。
            if (rule[^1] == '*')
            {
                if (toolName.StartsWith(rule[..^1], StringComparison.Ordinal)) return true;
            }
            else if (string.Equals(rule, toolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<string> Allowed(IEnumerable<string> toolNames)
        => toolNames.Where(IsAllowed).ToList();
}
