using AgentWorkbench.ApiContractTests.Support;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using AgentWorkbench.Knowledge.Local.Indexing;
using AgentWorkbench.Knowledge.Local.Retrieval;
using AgentWorkbench.Persistence;
using AgentWorkbench.Shared.Options;
using CommunityToolkit.VectorData.SqliteVec;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentWorkbench.ApiContractTests;

/// <summary>
/// 功能 5 的关键判据：删除后同一查询不能再命中。
/// 这里不 Mock 清理：用真实 ChunkIndexer 建 FTS、真实 HybridRetriever 检索，
/// 只把 Controller 的知识引擎接口委托给真实 indexer。
/// 用例编号对应 docs/验证与现状/F2/验证方式.md §10（D07）。
/// </summary>
public class DocumentDeleteRetrievalTests
{
    [Fact]
    public async Task D07_删除后真实FTS检索不再命中()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(h);

        var factory = new SqliteDbConnectionFactory(h.Paths);
        var embedding = new NullEmbeddingProvider();
        var vectorIndex = new VectorIndex(
            new SqliteVectorStore(h.Paths.ConnectionString), NullLogger<VectorIndex>.Instance);
        var indexer = new ChunkIndexer(
            h.Repository, factory, embedding, vectorIndex, NullLogger<ChunkIndexer>.Instance);
        var retriever = new HybridRetriever(
            h.Repository, factory, embedding, vectorIndex, new NullReranker(),
            Microsoft.Extensions.Options.Options.Create(new KnowledgeOptions()),
            NullLogger<HybridRetriever>.Instance);

        await indexer.IndexDocumentAsync(document.Id, CancellationToken.None);
        var before = await retriever.RetrieveAsync("zebraunique", 5, CancellationToken.None);
        Assert.Contains(before, hit => hit.DocumentId == document.Id);

        h.Knowledge
            .Setup(k => k.RemoveDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string id, CancellationToken ct) => indexer.RemoveDocumentAsync(id, ct));

        var result = await h.Controller.Delete(document.Id, CancellationToken.None);

        Assert.Equal(204, F2Harness.StatusOf(result));
        Assert.Empty(await retriever.RetrieveAsync("zebraunique", 5, CancellationToken.None));
        Assert.Equal(0, await indexer.CountIndexedChunksAsync());
        Assert.Null(await h.Repository.GetAsync(document.Id));
        Assert.Empty(await h.Repository.GetPagesAsync(document.Id));
        Assert.Empty(await h.Repository.GetChunksAsync(document.Id));
        Assert.False(File.Exists(document.StoredPath));
    }

    private static async Task<DocumentRecord> SeedAsync(F2Harness h)
    {
        var id = Guid.NewGuid().ToString("N");
        var storedPath = Path.Combine(h.UploadsDir, id + ".md");
        const string content = "zebraunique 只在这篇文档里出现";
        await File.WriteAllTextAsync(storedPath, content);

        var document = new DocumentRecord
        {
            Id = id,
            FileName = "retrieval-delete-test.md",
            StoredPath = storedPath,
            Sha256 = id,
            SizeBytes = content.Length,
            Status = DocumentStatus.Parsed,
            PageCount = 1,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            ParsedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        await h.Repository.InsertAsync(document);

        await h.Repository.ReplacePagesAsync(id, new[]
        {
            new DocumentPageRecord
            {
                DocumentId = id,
                PageNo = 1,
                RawText = content,
                CleanedText = content,
            },
        });
        await h.Repository.ReplaceChunksAsync(id, new[]
        {
            new ChunkRecord
            {
                DocumentId = id,
                Seq = 0,
                PageFrom = 1,
                PageTo = 1,
                Heading = null,
                Content = content,
                CharCount = content.Length,
                CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            },
        });

        return document;
    }
}
