using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Knowledge.Local.Tools;

/// <summary>
/// 工具 <c>search_knowledge</c>：在本地知识库检索与 query 相关的原文片段（带来源）。
/// 放在知识模块内，避免工具平台（ToolExecution）反向依赖知识库。
/// </summary>
public sealed class SearchKnowledgeTool : IAgentToolProvider
{
    public const string ToolName = "search_knowledge";

    private const int TopK = 5;
    private const int SnippetChars = 120;

    private readonly IServiceProvider _services;

    // 惰性解析：IKnowledgeEngine → LocalKnowledgeEngine → IRagService → IChatService，
    // 构造期直接注入会与 ChatService 形成 DI 循环。
    public SearchKnowledgeTool(IServiceProvider services) => _services = services;

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(
            async (string query, CancellationToken cancellationToken) =>
            {
                var knowledge = _services.GetRequiredService<IKnowledgeEngine>();
                var hits = await knowledge.RetrieveAsync(query, TopK, cancellationToken);
                if (hits.Count == 0)
                    return "无命中";
                return string.Join("\n", hits.Select(h => $"《{h.FileName}》片段#{h.Seq} :: {Snippet(h.Content)}"));
            },
            ToolName,
            "在本地知识库中检索与 query 相关的原文片段，返回带来源的命中。")
    ];

    private static string Snippet(string content)
        => content.Length <= SnippetChars ? content : content[..SnippetChars];
}
