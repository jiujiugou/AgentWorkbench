using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWorkbench.AgentHarness.Tools;

/// <summary>
/// 内置只读工具（get_current_time / calculator / search_knowledge / list_documents）。
/// 知识库/文档仓储通过 <see cref="IServiceProvider"/> 惰性解析，避免与 ChatService 形成 DI 循环。
/// </summary>
public sealed class BuiltinTools : IAgentToolProvider
{
    private readonly IServiceProvider _services;

    public BuiltinTools(IServiceProvider services) => _services = services;

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(
            () => DateTimeOffset.UtcNow.ToString("o"),
            "get_current_time",
            "返回当前 UTC 时间（ISO-8601）。"),

        AIFunctionFactory.Create(
            (string expression) => SafeCalculator.Evaluate(expression).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "calculator",
            "计算四则运算表达式，支持 + - * / 与括号，例如 (1+2)*3。"),

        AIFunctionFactory.Create(
            async (string query, CancellationToken cancellationToken) =>
            {
                var knowledge = _services.GetRequiredService<IKnowledgeEngine>();
                var hits = await knowledge.RetrieveAsync(query, 5, cancellationToken);
                if (hits.Count == 0)
                    return "无命中";
                return string.Join("\n", hits.Select(h => $"《{h.FileName}》片段#{h.Seq} :: {Snippet(h.Content)}"));
            },
            "search_knowledge",
            "在本地知识库中检索与 query 相关的原文片段，返回带来源的命中。"),

        AIFunctionFactory.Create(
            async (CancellationToken cancellationToken) =>
            {
                var documents = _services.GetRequiredService<IDocumentRepository>();
                var list = await documents.ListAsync();
                if (list.Count == 0)
                    return "知识库为空";
                return string.Join("\n", list.Select(d => $"{d.FileName} ({d.Status})"));
            },
            "list_documents",
            "列出知识库中的文档及状态（只读）。")
    ];

    private static string Snippet(string content)
        => content.Length <= 120 ? content : content[..120];
}
