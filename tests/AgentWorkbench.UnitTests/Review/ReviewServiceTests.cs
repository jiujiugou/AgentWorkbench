using System.Text.Json;
using AgentWorkbench.Agent;
using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using AgentWorkbench.Review;
using AgentWorkbench.Review.Comparison;
using AgentWorkbench.Review.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentWorkbench.UnitTests.Review;

public sealed class ReviewServiceTests
{
    private readonly Mock<IReviewRepository> _review = new();
    private readonly Mock<IDocumentRepository> _documents = new();
    private readonly Mock<IRuleRepository> _rules = new();
    private readonly Mock<IStructuredChatService> _chat = new();
    private readonly Mock<IReviewRunQueue> _queue = new();
    private readonly Mock<IReviewProgressNotifier> _notifier = new();
    private readonly List<long> _enqueued = new();
    private ReviewRunRecord _run = new();

    [Fact]
    public async Task GetCapabilities_WhenLlmNotConfigured_ReportsLlmFalse()
    {
        _chat.Setup(c => c.IsConfiguredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var service = CreateService();

        var caps = await service.GetCapabilitiesAsync(CancellationToken.None);

        Assert.True(caps.Extract);
        Assert.True(caps.Compare);
        Assert.False(caps.LlmExtract);
    }

    [Fact]
    public async Task CreateRun_DocumentMissing_Throws()
    {
        _documents.Setup(d => d.GetAsync("missing")).ReturnsAsync((DocumentRecord?)null);
        var service = CreateService();

        await Assert.ThrowsAsync<ReviewDocumentNotFoundException>(
            () => service.CreateRunAsync("missing", ReviewModes.Both, null, CancellationToken.None));
    }

    [Fact]
    public async Task CreateRun_DocumentNotParsed_Throws()
    {
        _documents.Setup(d => d.GetAsync("d")).ReturnsAsync(new DocumentRecord { Id = "d", Status = DocumentStatus.Pending });
        var service = CreateService();

        await Assert.ThrowsAsync<ReviewDocumentNotReadyException>(
            () => service.CreateRunAsync("d", ReviewModes.Both, null, CancellationToken.None));
    }

    [Fact]
    public async Task CreateRun_Success_NormalizesModeAndEnqueues()
    {
        _documents.Setup(d => d.GetAsync("d")).ReturnsAsync(new DocumentRecord { Id = "d", Status = DocumentStatus.Parsed });
        _review.Setup(r => r.CreateRunAsync("d", ReviewModes.Extract, null)).ReturnsAsync(new ReviewRunRecord { Id = 42, DocumentId = "d" });
        var service = CreateService();

        var runId = await service.CreateRunAsync("d", "EXTRACT", null, CancellationToken.None);

        Assert.Equal(42, runId);
        Assert.Contains(42L, _enqueued);
        _review.Verify(r => r.CreateRunAsync("d", "extract", null), Times.Once);
    }

    [Fact]
    public async Task CreateRun_UnknownMode_Throws()
    {
        _documents.Setup(d => d.GetAsync("d")).ReturnsAsync(new DocumentRecord { Id = "d", Status = DocumentStatus.Parsed });
        var service = CreateService();

        await Assert.ThrowsAsync<ReviewInvalidModeException>(
            () => service.CreateRunAsync("d", "llm", null, CancellationToken.None));

        _review.Verify(r => r.CreateRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        Assert.Empty(_enqueued);
    }

    [Fact]
    public async Task Execute_RunMissing_DoesNotUpdate()
    {
        _review.Setup(r => r.GetRunAsync(9)).ReturnsAsync((ReviewRunRecord?)null);
        var service = CreateService();

        await service.ExecuteAsync(9, CancellationToken.None);

        _review.Verify(r => r.UpdateRunAsync(It.IsAny<ReviewRunRecord>()), Times.Never);
    }

    [Fact]
    public async Task Execute_DocumentMissing_MarksFailed()
    {
        _run = new ReviewRunRecord { Id = 1, DocumentId = "d", Mode = ReviewModes.Both };
        _review.Setup(r => r.GetRunAsync(1)).ReturnsAsync(_run);
        _documents.Setup(d => d.GetAsync("d")).ReturnsAsync((DocumentRecord?)null);
        var service = CreateService();

        await service.ExecuteAsync(1, CancellationToken.None);

        Assert.Equal(ReviewStatuses.Failed, _run.Status);
        Assert.Equal("文档不存在", _run.Error);
    }

    [Fact]
    public async Task Execute_BothMode_PersistsElementsAndFindings()
    {
        var service = ArrangeRun(ReviewModes.Both, new StubExtractor([Element("amount", "100")]), new StubChecker([Finding()]));

        await service.ExecuteAsync(1, CancellationToken.None);

        Assert.Equal(ReviewStatuses.Succeeded, _run.Status);
        Assert.Contains("\"Name\":\"extract\"", _run.Steps);
        Assert.Contains("\"Name\":\"compare\"", _run.Steps);
        _review.Verify(r => r.ReplaceElementsAsync(1, It.IsAny<IReadOnlyList<ReviewElementRecord>>()), Times.Once);
        _review.Verify(r => r.ReplaceFindingsAsync(1, It.IsAny<IReadOnlyList<ReviewFindingRecord>>()), Times.Once);
    }

    [Fact]
    public async Task Execute_ExtractOnly_DoesNotPersistFindings()
    {
        var service = ArrangeRun(ReviewModes.Extract, new StubExtractor([Element("amount", "100")]), new StubChecker([Finding()]));

        await service.ExecuteAsync(1, CancellationToken.None);

        Assert.Equal(ReviewStatuses.Succeeded, _run.Status);
        _review.Verify(r => r.ReplaceElementsAsync(1, It.IsAny<IReadOnlyList<ReviewElementRecord>>()), Times.Once);
        _review.Verify(r => r.ReplaceFindingsAsync(1, It.IsAny<IReadOnlyList<ReviewFindingRecord>>()), Times.Never);
    }

    [Fact]
    public async Task Execute_CompareOnly_PersistsFindingsButNotElements()
    {
        var service = ArrangeRun(ReviewModes.Compare, new StubExtractor([Element("amount", "100")]), new StubChecker([Finding()]));

        await service.ExecuteAsync(1, CancellationToken.None);

        Assert.Equal(ReviewStatuses.Succeeded, _run.Status);
        _review.Verify(r => r.ReplaceElementsAsync(1, It.IsAny<IReadOnlyList<ReviewElementRecord>>()), Times.Never);
        _review.Verify(r => r.ReplaceFindingsAsync(1, It.IsAny<IReadOnlyList<ReviewFindingRecord>>()), Times.Once);
    }

    [Fact]
    public async Task Execute_WhenPersistenceThrows_MarksFailed()
    {
        var service = ArrangeRun(ReviewModes.Both, new StubExtractor([Element("amount", "100")]), new StubChecker([]));
        _review.Setup(r => r.ReplaceElementsAsync(1, It.IsAny<IReadOnlyList<ReviewElementRecord>>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await service.ExecuteAsync(1, CancellationToken.None);

        Assert.Equal(ReviewStatuses.Failed, _run.Status);
        Assert.Contains("boom", _run.Error);
    }

    [Fact]
    public async Task GetReport_SupportsCsvJsonAndMarkdown()
    {
        _review.Setup(r => r.GetRunAsync(1)).ReturnsAsync(new ReviewRunRecord { Id = 1, DocumentId = "d", Mode = "both", Steps = "[]", CreatedAt = "t" });
        _documents.Setup(d => d.GetAsync("d")).ReturnsAsync(new DocumentRecord { Id = "d", FileName = "f.pdf" });
        _review.Setup(r => r.GetElementsAsync(1)).ReturnsAsync(Array.Empty<ReviewElementRecord>());
        _review.Setup(r => r.GetFindingsAsync(1)).ReturnsAsync(Array.Empty<ReviewFindingRecord>());
        var service = CreateService();

        Assert.StartsWith("kind,", await service.GetReportAsync(1, "csv", CancellationToken.None));
        JsonDocument.Parse(await service.GetReportAsync(1, "json", CancellationToken.None));
        Assert.Contains("# 合同审查报告", await service.GetReportAsync(1, "md", CancellationToken.None));
        Assert.Contains("# 合同审查报告", await service.GetReportAsync(1, null!, CancellationToken.None));
    }

    [Fact]
    public async Task GetReport_RunMissing_Throws()
    {
        _review.Setup(r => r.GetRunAsync(5)).ReturnsAsync((ReviewRunRecord?)null);
        var service = CreateService();

        await Assert.ThrowsAsync<ReviewRunNotFoundException>(() => service.GetReportAsync(5, "md", CancellationToken.None));
    }

    [Fact]
    public async Task Rerun_Missing_ReturnsFalse()
    {
        _review.Setup(r => r.GetRunAsync(7)).ReturnsAsync((ReviewRunRecord?)null);
        var service = CreateService();

        Assert.False(await service.RerunAsync(7, CancellationToken.None));
    }

    [Fact]
    public async Task Rerun_Existing_ResetsStateAndEnqueues()
    {
        _run = new ReviewRunRecord { Id = 8, DocumentId = "d", Status = ReviewStatuses.Failed, Steps = "[{}]", Error = "e", LatencyMs = 5, FinishedAt = "f" };
        _review.Setup(r => r.GetRunAsync(8)).ReturnsAsync(_run);
        var service = CreateService();

        Assert.True(await service.RerunAsync(8, CancellationToken.None));

        Assert.Equal(ReviewStatuses.Pending, _run.Status);
        Assert.Equal("[]", _run.Steps);
        Assert.Null(_run.Error);
        Assert.Null(_run.LatencyMs);
        Assert.Null(_run.FinishedAt);
        Assert.Contains(8L, _enqueued);
        _review.Verify(r => r.UpdateRunAsync(_run), Times.Once);
    }

    private ReviewService ArrangeRun(string mode, IElementExtractor extractor, IRuleChecker checker)
    {
        _run = new ReviewRunRecord { Id = 1, DocumentId = "d", Mode = mode, Status = ReviewStatuses.Pending };
        _review.Setup(r => r.GetRunAsync(1)).ReturnsAsync(_run);
        _documents.Setup(d => d.GetAsync("d")).ReturnsAsync(new DocumentRecord { Id = "d", FileName = "f.pdf", Status = DocumentStatus.Parsed });
        _documents.Setup(d => d.GetChunksAsync("d")).ReturnsAsync(new List<ChunkRecord> { Chunk("amount 100") });
        _chat.Setup(c => c.IsConfiguredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        return CreateService([extractor], [checker]);
    }

    private ReviewService CreateService(
        IEnumerable<IElementExtractor>? extractors = null,
        IEnumerable<IRuleChecker>? checkers = null)
    {
        _enqueued.Clear();
        _queue.Setup(q => q.EnqueueAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((long id, CancellationToken _) => { _enqueued.Add(id); return default; });
        _review.Setup(r => r.UpdateRunAsync(It.IsAny<ReviewRunRecord>())).Returns(Task.CompletedTask);
        _review.Setup(r => r.ReplaceElementsAsync(It.IsAny<long>(), It.IsAny<IReadOnlyList<ReviewElementRecord>>())).Returns(Task.CompletedTask);
        _review.Setup(r => r.ReplaceFindingsAsync(It.IsAny<long>(), It.IsAny<IReadOnlyList<ReviewFindingRecord>>())).Returns(Task.CompletedTask);
        _rules.Setup(r => r.ListEnabledAsync()).ReturnsAsync(Array.Empty<RuleRecord>());

        var extractorRegistry = new ElementExtractorRegistry(extractors ?? [new StubExtractor([])], NullLogger<ElementExtractorRegistry>.Instance);
        var checkerRegistry = new RuleCheckerRegistry(checkers ?? []);
        return new ReviewService(
            _review.Object, _documents.Object, _rules.Object, extractorRegistry, checkerRegistry,
            _chat.Object, new ReviewReportWriter(), _queue.Object, _notifier.Object, NullLogger<ReviewService>.Instance);
    }

    private static ChunkRecord Chunk(string content) => new()
    {
        Id = 1, DocumentId = "d", Seq = 0, PageFrom = 1, PageTo = 1,
        Content = content, CharCount = content.Length, CreatedAt = "t"
    };

    private static ElementDraft Element(string key, string value)
        => new(key, key, value, "text", null, "stub", 0.9, null, null, null, null, null);

    private static FindingDraft Finding()
        => new(null, FindingSeverities.Low, "cat", "title", null, null, null, null, null, null, null, null);

    private sealed class StubExtractor(IReadOnlyList<ElementDraft> drafts) : IElementExtractor
    {
        public string Name => "stub";

        public Task<IReadOnlyList<ElementDraft>> ExtractAsync(ReviewContext context, CancellationToken cancellationToken)
            => Task.FromResult(drafts);
    }

    private sealed class StubChecker(IReadOnlyList<FindingDraft> findings) : IRuleChecker
    {
        public string Name => "stub";

        public IEnumerable<FindingDraft> Check(ReviewContext context, IReadOnlyList<RuleRecord> rules, IReadOnlyList<ElementDraft> elements)
            => findings;
    }
}
