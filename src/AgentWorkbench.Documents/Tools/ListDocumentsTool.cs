using AgentWorkbench.Persistence;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWorkbench.Documents.Tools;

/// <summary>
/// 工具 <c>list_documents</c>：列出知识库中的文档及状态（只读）。
/// 放在文档模块内，避免工具平台（ToolExecution）反向依赖文档仓储。
/// </summary>
public sealed class ListDocumentsTool : IAgentToolProvider
{
    public const string ToolName = "list_documents";

    private readonly IServiceProvider _services;

    public ListDocumentsTool(IServiceProvider services) => _services = services;

    public IReadOnlyList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(
            async (CancellationToken cancellationToken) =>
            {
                var documents = _services.GetRequiredService<IDocumentRepository>();
                var list = await documents.ListAsync();
                if (list.Count == 0)
                    return "知识库为空";
                return string.Join("\n", list.Select(d => $"{d.FileName} ({d.Status})"));
            },
            ToolName,
            "列出知识库中的文档及状态（只读）。")
    ];
}
