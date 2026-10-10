using AgentWorkbench.Domain;
using AgentWorkbench.Review;
using AgentWorkbench.Review.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentWorkbench.UnitTests.Review.Extraction;

public sealed class ElementExtractorRegistryTests
{
    [Fact]
    public async Task ExtractAll_MergesDistinctKeys()
    {
        var registry = Registry(new StubExtractor("a", Element("seller", "甲")), new StubExtractor("b", Element("buyer", "乙")));

        var drafts = await registry.ExtractAllAsync(Context(), CancellationToken.None);

        Assert.Equal(2, drafts.Count);
        Assert.Contains(drafts, d => d.Key == "seller");
        Assert.Contains(drafts, d => d.Key == "buyer");
    }

    [Fact]
    public async Task ExtractAll_LaterExtractorOverridesSameKey()
    {
        var registry = Registry(new StubExtractor("rule", Element("amount", "100")), new StubExtractor("llm", Element("amount", "200")));

        var drafts = await registry.ExtractAllAsync(Context(), CancellationToken.None);

        Assert.Equal("200", Assert.Single(drafts).Value);
    }

    [Fact]
    public async Task ExtractAll_FailingExtractor_IsSkipped()
    {
        var registry = Registry(new ThrowingExtractor("boom", new InvalidOperationException("x")), new StubExtractor("ok", Element("seller", "甲")));

        var drafts = await registry.ExtractAllAsync(Context(), CancellationToken.None);

        Assert.Single(drafts);
    }

    [Fact]
    public async Task ExtractAll_Cancellation_Propagates()
    {
        var registry = Registry(new ThrowingExtractor("cancel", new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ExtractAllAsync(Context(), CancellationToken.None));
    }

    private static ElementExtractorRegistry Registry(params IElementExtractor[] extractors)
        => new(extractors, NullLogger<ElementExtractorRegistry>.Instance);

    private static ReviewContext Context() => new("doc", "f.pdf", Array.Empty<ChunkRecord>());

    private static ElementDraft Element(string key, string value)
        => new(key, key, value, "text", null, "stub", 0.5, null, null, null, null, null);

    private sealed class StubExtractor(string name, params ElementDraft[] drafts) : IElementExtractor
    {
        public string Name { get; } = name;

        public Task<IReadOnlyList<ElementDraft>> ExtractAsync(ReviewContext context, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ElementDraft>>(drafts);
    }

    private sealed class ThrowingExtractor(string name, Exception exception) : IElementExtractor
    {
        public string Name { get; } = name;

        public Task<IReadOnlyList<ElementDraft>> ExtractAsync(ReviewContext context, CancellationToken cancellationToken)
            => Task.FromException<IReadOnlyList<ElementDraft>>(exception);
    }
}
