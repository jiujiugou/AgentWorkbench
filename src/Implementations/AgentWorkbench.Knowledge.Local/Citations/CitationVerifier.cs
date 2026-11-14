using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local.Retrieval;

namespace AgentWorkbench.Knowledge.Local.Citations;

/// <summary>
/// 引用两级校验：一级看编号是否存在，二级看内容对不对得上。
/// </summary>
/// <remarks>
/// <para>
/// 一级只保证"号码存在"，二级才防"号码存在但内容对不上"。后者不是编造，是<b>误挂</b>——
/// 同样假，而且更隐蔽：用户看到角标、打开原文、发现那句话根本不在那里。
/// </para>
/// <para>
/// 二级用字符覆盖率而不是让模型自评：模型当裁判不可复算，同一个答案跑两次可能得出两个结论。
/// 覆盖率完全由输入决定，同一份文本永远得到同一个数。
/// </para>
/// <para>
/// <b>阈值可空</b>：未配置时只记录覆盖率数值，一律判 <see cref="CiteCheck.Ok"/>，不标黄。
/// 用 0 表示"未配置"会让"没判定"和"判定通过"长得完全一样——这正是这套东西要消灭的歧义。
/// </para>
/// </remarks>
public static class CitationVerifier
{
    /// <summary>
    /// 校验一次作答。
    /// </summary>
    /// <param name="answer">模型完整输出（原始，含 <c>[n]</c> 标注）。</param>
    /// <param name="evidence">本次用于作答的证据，下标 +1 就是模型看到的编号；为空表示没有可用证据。</param>
    /// <param name="coverageThreshold">二级校验的覆盖率下限；<c>null</c> 表示未测出来，只记录不判定。</param>
    public static CitationReport Verify(
        string? answer,
        IReadOnlyList<RetrievalHit>? evidence,
        double? coverageThreshold = null)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return CitationReport.Empty(answer ?? string.Empty);

        var parsed = CitationParser.Parse(answer);
        var pool = evidence ?? [];
        var contentCache = new Dictionary<long, HashSet<string>>();

        var citations = new List<Citation>(parsed.Raw.Count);
        foreach (var (sentenceIndex, evidenceNo) in parsed.Raw)
        {
            var inRange = evidenceNo >= 1 && evidenceNo <= pool.Count;
            var hit = inRange ? pool[evidenceNo - 1] : null;

            var coverage = hit is null ? (double?)null : Coverage(parsed.Sentences[sentenceIndex], hit, contentCache);

            var check = CiteCheck.Ok;
            if (hit is null)
                check = CiteCheck.OutOfRange;
            else if (coverageThreshold is { } threshold && coverage < threshold)
                check = CiteCheck.Unsupported;

            citations.Add(new Citation(
                sentenceIndex,
                evidenceNo,
                hit?.FileName ?? string.Empty,
                hit?.Seq ?? 0,
                hit?.ChunkId ?? 0,
                check,
                coverage));
        }

        var citedSentences = citations
            .Where(c => c.Check != CiteCheck.OutOfRange)
            .Select(c => c.SentenceIndex)
            .Distinct()
            .Count();

        var sentenceCount = parsed.Sentences.Count;
        var cleaned = CitationParser.KeepCitations(answer, no => no >= 1 && no <= pool.Count);

        return new CitationReport(
            sentenceCount,
            citedSentences,
            sentenceCount - citedSentences,
            citations.Count(c => c.Check == CiteCheck.OutOfRange),
            citations.Count(c => c.Check == CiteCheck.Unsupported),
            cleaned,
            citations);
    }

    /// <summary>
    /// 一句与一块的字符覆盖率：该句的两字组合有多少出现在这一块里。
    /// </summary>
    /// <remarks>
    /// 用与索引同一套 <see cref="BigramTokenizer"/>，不另造一套比对规则——
    /// 两套规则会让"检索判定相关"和"引用判定对得上"给出互相矛盾的结论。
    /// 模型用自己的话复述时覆盖率天然偏低，所以这个数只适合横向对比，不适合当绝对值判据。
    /// </remarks>
    private static double Coverage(string sentence, RetrievalHit hit, Dictionary<long, HashSet<string>> cache)
    {
        var tokens = BigramTokenizer.Tokens(CitationParser.StripCitations(sentence));
        if (tokens.Count == 0)
            return 0;

        if (!cache.TryGetValue(hit.ChunkId, out var pool))
        {
            pool = new HashSet<string>(BigramTokenizer.Tokens(hit.Content), StringComparer.OrdinalIgnoreCase);
            cache[hit.ChunkId] = pool;
        }

        var matched = 0;
        foreach (var token in tokens)
        {
            if (pool.Contains(token))
                matched++;
        }

        return (double)matched / tokens.Count;
    }
}
