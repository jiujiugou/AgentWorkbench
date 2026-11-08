using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local.Retrieval;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Knowledge.Local;

/// <summary>知识检索的对外门面。</summary>
/// <remarks>
/// <b>这里没有「问答」。</b>问答只有一条路径：<c>AgentHub.Ask</c> → harness →
/// <c>search_knowledge</c> 工具 → 本接口。曾经另有一条 <c>POST /api/knowledge/ask</c>
/// 直连生成，它不留 run、不写轨迹、引用格式还与工具那条不同，前端零调用，
/// 已删除——不要加回来。要改问答行为，改 harness 或工具，不要在这里加生成逻辑。
/// </remarks>
public interface IRagService
{
    Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class RagService : IRagService
{
    private readonly HybridRetriever _retriever;
    private readonly KnowledgeOptions _options;

    public RagService(HybridRetriever retriever, IOptions<KnowledgeOptions> options)
    {
        _retriever = retriever;
        _options = options.Value;
    }

    public Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(
        string query, int topK, CancellationToken cancellationToken)
        => _retriever.RetrieveAsync(query, topK <= 0 ? _options.TopK : topK, cancellationToken);
}
