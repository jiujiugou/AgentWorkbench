using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local.Retrieval;
using AgentWorkbench.Agent;
using AgentWorkbench.Shared.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Knowledge.Local;

/// <summary>检索增强问答：检索 → 拼上下文（强制引用）→ 流式生成；无命中则拒答。</summary>
public interface IRagService
{
    Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamAnswerAsync(
        string query,
        IReadOnlyList<ChatTurn> history,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class RagService : IRagService
{
    private readonly IChatService _chat;
    private readonly HybridRetriever _retriever;
    private readonly KnowledgeOptions _options;
    private readonly ILogger<RagService> _logger;

    public RagService(
        IChatService chat,
        HybridRetriever retriever,
        IOptions<KnowledgeOptions> options,
        ILogger<RagService> logger)
    {
        _chat = chat;
        _retriever = retriever;
        _options = options.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(
        string query, int topK, CancellationToken cancellationToken)
        => _retriever.RetrieveAsync(query, topK <= 0 ? _options.TopK : topK, cancellationToken);

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        string query,
        IReadOnlyList<ChatTurn> history,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug("StreamAnswerAsync started: Query='{Query}' HistoryTurns={HistoryCount}", query, history.Count);

        var hits = await _retriever.RetrieveAsync(query, _options.TopK, cancellationToken);
        if (hits.Count == 0 || hits[0].FusedScore <= _options.MinFusedScore)
        {
            _logger.LogInformation("知识库无命中，拒答：{Query} in {ElapsedMs}ms", query, sw.ElapsedMilliseconds);
            yield return "资料不足以回答该问题。";
            yield break;
        }

        _logger.LogInformation("StreamAnswerAsync retrieved: Query='{Query}' Hits={HitCount} TopScore={TopScore} ElapsedMs={ElapsedMs}",
            query, hits.Count, hits[0].FusedScore, sw.ElapsedMilliseconds);

        var systemPrompt = BuildSystemPrompt(hits);
        await foreach (var delta in _chat.StreamAsync(history, query, cancellationToken, systemPrompt))
            yield return delta;

        _logger.LogInformation("StreamAnswerAsync completed: Query='{Query}' ElapsedMs={ElapsedMs}", query, sw.ElapsedMilliseconds);
    }

    private static string BuildSystemPrompt(IReadOnlyList<RetrievalHit> hits)
    {
        var builder = new StringBuilder();
        // 场景无关：这里只约束「依据资料回答 + 强制引用 + 不足则拒答」，
        // 具体领域人格交给调用方（如工作流的提示词版本）注入，避免把垂直场景写死在检索模块里。
        builder.AppendLine("你是严谨的资料问答助手。只能依据下面【资料】回答，不得编造。");
        builder.AppendLine("每个结论后用 [编号] 标注出处，例如 [1]；若资料不足以回答，直接回答“资料不足以回答该问题”。");
        builder.AppendLine();
        builder.AppendLine("【资料】");
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            builder.Append('[').Append(i + 1).Append("] 《").Append(hit.FileName).Append("》 ");
            if (!string.IsNullOrWhiteSpace(hit.Heading))
                builder.Append(hit.Heading).Append(' ');
            builder.Append("(片段#").Append(hit.Seq).Append(", p").Append(hit.PageFrom).Append('-').Append(hit.PageTo).Append(')').AppendLine();
            builder.AppendLine(hit.Content);
            builder.AppendLine();
        }
        return builder.ToString();
    }
}
