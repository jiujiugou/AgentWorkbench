using System.Text.Json;
using AgentWorkbench.Agent;
using AgentWorkbench.Domain;
using AgentWorkbench.Review;
using AgentWorkbench.Review.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentWorkbench.UnitTests.Review.Extraction;

public sealed class LlmElementExtractorTests
{
    [Fact]
    public async Task Extract_WhenLlmNotConfigured_ReturnsEmptyAndSkipsCall()
    {
        var chat = new FakeStructuredChat { Configured = false };
        var extractor = new LlmElementExtractor(chat, NullLogger<LlmElementExtractor>.Instance);

        var drafts = await extractor.ExtractAsync(Context("出卖人：甲公司"), CancellationToken.None);

        Assert.Empty(drafts);
        Assert.False(chat.GetJsonCalled);
    }

    [Fact]
    public async Task Extract_WhenLlmThrows_ReturnsEmpty()
    {
        var chat = new FakeStructuredChat { Configured = true, Throw = new InvalidOperationException("boom") };
        var extractor = new LlmElementExtractor(chat, NullLogger<LlmElementExtractor>.Instance);

        var drafts = await extractor.ExtractAsync(Context("出卖人：甲公司"), CancellationToken.None);

        Assert.Empty(drafts);
    }

    [Fact]
    public async Task Extract_WhenLlmReturnsNull_ReturnsEmpty()
    {
        var chat = new FakeStructuredChat { Configured = true, Json = null };
        var extractor = new LlmElementExtractor(chat, NullLogger<LlmElementExtractor>.Instance);

        Assert.Empty(await extractor.ExtractAsync(Context("出卖人：甲公司"), CancellationToken.None));
    }

    [Fact]
    public async Task Extract_ValidJson_MapsNonEmptyValuesOnly()
    {
        var chat = new FakeStructuredChat
        {
            Configured = true,
            Json = "{\"seller\":\"华成公司\",\"buyer\":\"\",\"amount\":\"128000\"}"
        };
        var extractor = new LlmElementExtractor(chat, NullLogger<LlmElementExtractor>.Instance);

        var drafts = await extractor.ExtractAsync(Context("出卖人：华成公司 金额：128000 元"), CancellationToken.None);

        Assert.Equal(2, drafts.Count);
        Assert.Contains(drafts, d => d.Key == "seller" && d.Value == "华成公司");
        Assert.Contains(drafts, d => d.Key == "amount" && d.Value == "128000");
        Assert.DoesNotContain(drafts, d => d.Key == "buyer");
        Assert.All(drafts, d => Assert.Equal("llm", d.Source));
        Assert.All(drafts, d => Assert.Equal(0.8, d.Confidence));
    }

    [Fact]
    public async Task Extract_ValidJson_LocatesEvidenceInChunks()
    {
        var chat = new FakeStructuredChat { Configured = true, Json = "{\"seller\":\"华成公司\"}" };
        var extractor = new LlmElementExtractor(chat, NullLogger<LlmElementExtractor>.Instance);

        var seller = Assert.Single(await extractor.ExtractAsync(Context("出卖人：华成公司"), CancellationToken.None));

        Assert.Equal(1L, seller.ChunkId!.Value);
        Assert.Equal(0, seller.Seq!.Value);
        Assert.NotNull(seller.Evidence);
    }

    [Fact]
    public async Task Extract_BuildsPromptWithContractText()
    {
        var chat = new FakeStructuredChat { Configured = true, Json = "{}" };
        var extractor = new LlmElementExtractor(chat, NullLogger<LlmElementExtractor>.Instance);

        await extractor.ExtractAsync(Context("甲方：华成公司"), CancellationToken.None);

        Assert.NotNull(chat.LastUserPrompt);
        Assert.Contains("【合同文本】", chat.LastUserPrompt);
        Assert.Contains("甲方：华成公司", chat.LastUserPrompt);
    }

    private static ReviewContext Context(params string[] contents)
    {
        var chunks = contents.Select((c, i) => new ChunkRecord
        {
            Id = i + 1,
            DocumentId = "doc",
            Seq = i,
            PageFrom = 1,
            PageTo = 1,
            Content = c,
            CharCount = c.Length,
            CreatedAt = "t"
        }).ToList();
        return new ReviewContext("doc", "f.pdf", chunks);
    }

    private sealed class FakeStructuredChat : IStructuredChatService
    {
        public bool Configured { get; set; }

        public string? Json { get; set; }

        public Exception? Throw { get; set; }

        public bool GetJsonCalled { get; private set; }

        public string? LastUserPrompt { get; private set; }

        public Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default) => Task.FromResult(Configured);

        public Task<T?> GetJsonAsync<T>(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
        {
            GetJsonCalled = true;
            LastUserPrompt = userPrompt;
            if (Throw is not null)
                throw Throw;
            return Task.FromResult(Json is null ? default : JsonSerializer.Deserialize<T>(Json));
        }
    }
}
