using System.Globalization;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Memory.Persistence.Tools;

/// <summary>
/// 工具 <c>recall</c>：按关键词召回本作用域（+ 全局）内已记住的信息。
/// </summary>
/// <remarks>
/// 返回候选原文摘要（工具结果）；「注入几条、每条多长、占多少预算」是 <c>Context</c> 的职责，不在本模块。
/// </remarks>
public sealed class RecallTool : IAgentToolProvider
{
    public const string ToolName = "recall";

    private const int SnippetChars = 200;

    private readonly ILongTermMemoryStore _store;
    private readonly IMemoryScopeAccessor _scopeAccessor;
    private readonly MemoryOptions _options;

    public RecallTool(
        ILongTermMemoryStore store, IMemoryScopeAccessor scopeAccessor, IOptions<MemoryOptions> options)
    {
        _store = store;
        _scopeAccessor = scopeAccessor;
        _options = options.Value;
    }

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(
            async (string? query = null, int? limit = null, string? kind = null,
                CancellationToken cancellationToken = default) =>
            {
                if (!_options.LongTermEnabled)
                    return "长期记忆未启用。";

                var scope = MemoryToolScope.Resolve(_scopeAccessor, _options, global: false);
                if (scope is null)
                    return "当前不在任何会话内；如需查询全局记忆请让调用方配置 global 作用域。";

                var items = await _store.RecallAsync(
                    new MemoryQuery(
                        scope,
                        string.IsNullOrWhiteSpace(query) ? null : query,
                        IncludeGlobal: true,
                        CandidateK: limit ?? 0,
                        Kinds: string.IsNullOrWhiteSpace(kind) ? null : [MemoryKinds.Normalize(kind)]),
                    cancellationToken);

                if (items.Count == 0)
                    return "无命中";

                return string.Join("\n", items.Select(Format));
            },
            ToolName,
            "召回已记住的信息（当前会话 + 全局）。query 为关键词过滤，limit 为返回条数上限，kind 可按 fact / preference / decision / note 过滤。")
    ];

    private static string Format(MemoryItem item)
    {
        var scope = item.Scope.Type == MemoryScopeTypes.Session ? "session" : item.Scope.Type;
        var key = item.Key is null ? string.Empty : $"key={item.Key} ";
        var content = item.Content.Length <= SnippetChars ? item.Content : item.Content[..SnippetChars];
        return string.Format(
            CultureInfo.InvariantCulture, "[{0}] {1}{2} ({3}, imp={4:0.##}) :: {5}",
            item.Id, key, item.Kind, scope, item.Importance, content);
    }
}
