using AgentWorkbench.Knowledge.Abstractions.Retrieval;

namespace AgentWorkbench.Context;

/// <summary>上下文组装：把一次模型调用要看的输入拼成最终 user 文本（纯逻辑，无 IO）。</summary>
public interface IContextBuilder
{
    /// <summary>把检索命中拼到用户输入之前；无命中时原样返回。</summary>
    string Compose(string input, IReadOnlyList<RetrievalHit> hits);
}

/// <inheritdoc />
public sealed class ContextBuilder : IContextBuilder
{
    public string Compose(string input, IReadOnlyList<RetrievalHit> hits)
    {
        var context = BuildRetrievalContext(hits);
        return context.Length == 0 ? input : context + "\n\n" + input;
    }

    private static string BuildRetrievalContext(IReadOnlyList<RetrievalHit> hits)
    {
        if (hits.Count == 0)
            return string.Empty;
        var lines = hits.Select(h => $"《{h.FileName}》片段#{h.Seq} (p{h.PageFrom}-{h.PageTo}) :: {Snippet(h.Content)}");
        return "【知识库检索片段】仅供回答参考，需在结论中标注来源：\n" + string.Join("\n", lines);
    }

    private static string Snippet(string content)
        => content.Length <= 120 ? content : content[..120];
}
