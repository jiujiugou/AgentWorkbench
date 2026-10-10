namespace AgentWorkbench.Review.Extraction;

/// <summary>要素抽取扩展点：新增要素来源 = 新增实现。</summary>
public interface IElementExtractor
{
    string Name { get; }

    Task<IReadOnlyList<ElementDraft>> ExtractAsync(ReviewContext context, CancellationToken cancellationToken);
}

/// <summary>要素抽取器注册表：聚合所有实现，同 key 后注册者覆盖（LLM 覆盖规则）。</summary>
public sealed class ElementExtractorRegistry
{
    private readonly IReadOnlyList<IElementExtractor> _extractors;
    private readonly ILogger<ElementExtractorRegistry> _logger;

    public ElementExtractorRegistry(IEnumerable<IElementExtractor> extractors, ILogger<ElementExtractorRegistry> logger)
    {
        _extractors = extractors.ToList();
        _logger = logger;
    }

    public async Task<IReadOnlyList<ElementDraft>> ExtractAllAsync(ReviewContext context, CancellationToken cancellationToken)
    {
        var merged = new List<ElementDraft>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var extractor in _extractors)
        {
            IReadOnlyList<ElementDraft> drafts;
            try
            {
                drafts = await extractor.ExtractAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "要素抽取器 {Extractor} 失败，跳过", extractor.Name);
                continue;
            }

            foreach (var draft in drafts)
            {
                if (index.TryGetValue(draft.Key, out var at))
                    merged[at] = draft;
                else
                {
                    index[draft.Key] = merged.Count;
                    merged.Add(draft);
                }
            }
        }

        _logger.LogInformation("要素抽取完成：{Count} 项（来自 {Extractors} 个抽取器）", merged.Count, _extractors.Count);
        return merged;
    }
}
