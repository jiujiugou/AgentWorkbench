using AgentWorkbench.ApiContractTests.Support;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using Moq;
using Xunit;

namespace AgentWorkbench.ApiContractTests;

/// <summary>
/// 功能 5「删掉它」：索引清干净才删记录；清不干净返回 409 并保留记录；
/// 删除成功后列表、页/块、文件一起消失。
/// 用例编号对应 docs/验证与现状/F2/验证方式.md §10（D01–D06）。
/// </summary>
public class DocumentDeleteTests
{
    [Fact]
    public async Task D01_删除已解析文档_记录页块与文件一起消失()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(h, DocumentStatus.Parsed);
        h.Knowledge
            .Setup(k => k.RemoveDocumentAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoveResult(0, 0, null));

        var result = await h.Controller.Delete(document.Id, CancellationToken.None);

        Assert.Equal(204, F2Harness.StatusOf(result));
        Assert.Null(await h.Repository.GetAsync(document.Id));
        Assert.DoesNotContain(await h.Repository.ListAsync(), d => d.Id == document.Id);
        Assert.Empty(await h.Repository.GetPagesAsync(document.Id));
        Assert.Empty(await h.Repository.GetChunksAsync(document.Id));
        Assert.False(File.Exists(document.StoredPath));
        Assert.Empty(h.StoredFiles());
        Assert.Equal(0, h.Queue.Count);
        h.Knowledge.Verify(
            k => k.RemoveDocumentAsync(document.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task D02_删除不存在的文档_404且无副作用()
    {
        using var h = new F2Harness();
        var before = await h.SnapshotAsync();

        var result = await h.Controller.Delete("not-a-real-id", CancellationToken.None);

        Assert.Equal(404, F2Harness.StatusOf(result));
        Assert.Equal(before, await h.SnapshotAsync());
        h.Knowledge.Verify(
            k => k.RemoveDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task D03_FTS残留_409并保留记录页块与文件()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(h, DocumentStatus.Parsed);
        h.Knowledge
            .Setup(k => k.RemoveDocumentAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoveResult(FtsRemaining: 1, VectorsRemaining: 0, Failure: null));

        var result = await h.Controller.Delete(document.Id, CancellationToken.None);

        Assert.Equal(409, F2Harness.StatusOf(result));
        Assert.Equal(1, F2Harness.JsonOf(result).GetProperty("ftsRemaining").GetInt32());
        Assert.NotNull(await h.Repository.GetAsync(document.Id));
        Assert.Contains(await h.Repository.ListAsync(), d => d.Id == document.Id);
        Assert.Single(await h.Repository.GetPagesAsync(document.Id));
        Assert.Single(await h.Repository.GetChunksAsync(document.Id));
        Assert.True(File.Exists(document.StoredPath));
        h.Knowledge.Verify(
            k => k.RemoveDocumentAsync(document.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task D04_向量删除失败_409并保留记录页块与文件()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(h, DocumentStatus.Parsed);
        var failure = new EmbeddingFailure(
            EmbeddingFailureCode.VectorDeleteFailed, FailureKind.Permanent, "向量删除失败");
        h.Knowledge
            .Setup(k => k.RemoveDocumentAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoveResult(FtsRemaining: 0, VectorsRemaining: 1, Failure: failure));

        var result = await h.Controller.Delete(document.Id, CancellationToken.None);

        Assert.Equal(409, F2Harness.StatusOf(result));
        Assert.Equal(1, F2Harness.JsonOf(result).GetProperty("vectorsRemaining").GetInt32());
        Assert.NotNull(await h.Repository.GetAsync(document.Id));
        Assert.Contains(await h.Repository.ListAsync(), d => d.Id == document.Id);
        Assert.Single(await h.Repository.GetPagesAsync(document.Id));
        Assert.Single(await h.Repository.GetChunksAsync(document.Id));
        Assert.True(File.Exists(document.StoredPath));
        h.Knowledge.Verify(
            k => k.RemoveDocumentAsync(document.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task D05_失败文档也能删除_三层清理完成()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(h, DocumentStatus.Failed, error: "解析失败：文件损坏");
        h.Knowledge
            .Setup(k => k.RemoveDocumentAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoveResult(0, 0, null));

        var result = await h.Controller.Delete(document.Id, CancellationToken.None);

        Assert.Equal(204, F2Harness.StatusOf(result));
        Assert.Null(await h.Repository.GetAsync(document.Id));
        Assert.Empty(await h.Repository.GetPagesAsync(document.Id));
        Assert.Empty(await h.Repository.GetChunksAsync(document.Id));
        Assert.False(File.Exists(document.StoredPath));
        h.Knowledge.Verify(
            k => k.RemoveDocumentAsync(document.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task D06_重复删除_第一次204第二次404且只清理一次()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(h, DocumentStatus.Parsed);
        h.Knowledge
            .Setup(k => k.RemoveDocumentAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoveResult(0, 0, null));

        var first = await h.Controller.Delete(document.Id, CancellationToken.None);
        var second = await h.Controller.Delete(document.Id, CancellationToken.None);

        Assert.Equal(204, F2Harness.StatusOf(first));
        Assert.Equal(404, F2Harness.StatusOf(second));
        h.Knowledge.Verify(
            k => k.RemoveDocumentAsync(document.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task<DocumentRecord> SeedAsync(
        F2Harness h, string status, string? error = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var storedPath = Path.Combine(h.UploadsDir, id + ".md");
        var content = "删除前的正文";
        await File.WriteAllTextAsync(storedPath, content);

        var document = new DocumentRecord
        {
            Id = id,
            FileName = "delete-test.md",
            StoredPath = storedPath,
            Sha256 = id,
            SizeBytes = content.Length,
            Status = status,
            PageCount = 1,
            Error = error,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            ParsedAt = status == DocumentStatus.Parsed ? DateTimeOffset.UtcNow.ToString("o") : null,
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
