namespace AgentWorkbench.Knowledge.Abstractions.Retrieval;

/// <summary>重排抽象：对融合后的候选重排（P1 默认关，占位为 <see cref="NullReranker"/>）。</summary>
public interface IReranker
{
    bool Enabled { get; }

    Task<IReadOnlyList<RetrievalHit>> RerankAsync(
        string query, IReadOnlyList<RetrievalHit> hits, int topK, CancellationToken cancellationToken);
}

/// <summary>空实现：直接截断 top-K（P1 默认）。</summary>
public sealed class NullReranker : IReranker
{
    public bool Enabled => false;

    public Task<IReadOnlyList<RetrievalHit>> RerankAsync(
        string query, IReadOnlyList<RetrievalHit> hits, int topK, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<RetrievalHit>>(hits.Take(topK).ToList());
}
