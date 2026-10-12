using System.Globalization;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Memory.Persistence.Tools;

/// <summary>
/// 工具 <c>remember</c>：把一条需要长期保留的信息写入记忆库。
/// 放在 Memory.Persistence 内，避免工具平台（ToolExecution）反向依赖记忆模块。
/// </summary>
/// <remarks>
/// 返回的是**工具结果**（给模型看的一句话），不是系统注入的 prompt —— 系统注入由 <c>Context</c> 负责。
/// </remarks>
public sealed class RememberTool : IAgentToolProvider
{
    public const string ToolName = "remember";

    private readonly ILongTermMemoryStore _store;
    private readonly IMemoryScopeAccessor _scopeAccessor;
    private readonly MemoryOptions _options;

    public RememberTool(
        ILongTermMemoryStore store, IMemoryScopeAccessor scopeAccessor, IOptions<MemoryOptions> options)
    {
        _store = store;
        _scopeAccessor = scopeAccessor;
        _options = options.Value;
    }

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(
            async (string content, string? key = null, string? kind = null, double? importance = null,
                bool? global = null, CancellationToken cancellationToken = default) =>
            {
                if (!_options.LongTermEnabled)
                    return "长期记忆未启用。";

                var scope = MemoryToolScope.Resolve(_scopeAccessor, _options, global == true);
                if (scope is null)
                    return "当前不在任何会话内，无法确定记忆作用域；如需跨会话记忆请传 global=true。";

                try
                {
                    var item = await _store.RememberAsync(
                        new MemoryWrite(
                            scope,
                            content ?? string.Empty,
                            key,
                            kind ?? MemoryKinds.Fact,
                            importance ?? 0.5,
                            // 会话作用域的 id 就是来源会话，顺手带上便于追溯。
                            SourceSessionId: scope.Type == MemoryScopeTypes.Session ? scope.Id : null),
                        cancellationToken);

                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "已记住 #{0}（scope={1}，kind={2}，importance={3:0.##}）",
                        item.Id, item.Scope.Type, item.Kind, item.Importance);
                }
                catch (ArgumentException ex)
                {
                    // 内容为空 / 超长是调用方能修的问题，回一句话让模型自己改正，而不是抛出去把整轮 run 打挂。
                    return $"未记住：{ex.Message}";
                }
            },
            ToolName,
            "把需要长期保留的信息写入记忆库。content 必填；key 相同则覆盖同一条记忆；" +
            "global=true 写入所有会话都可见的全局记忆。kind 可填 fact / preference / decision / note。")
    ];
}
