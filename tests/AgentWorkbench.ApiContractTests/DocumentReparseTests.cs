using System.Text.Json;
using AgentWorkbench.ApiContractTests.Support;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using Xunit;

namespace AgentWorkbench.ApiContractTests;

/// <summary>
/// 功能 4「失败了再试一次」：reparse 只负责重新排队——置 pending、清 error、入队，
/// 不提前删除旧页/块；不存在的 id 必须 404 且无副作用。
/// 用例编号对应 docs/验证与现状/F2/验证方式.md §9（R01–R06）。
/// </summary>
public class DocumentReparseTests
{
    [Fact]
    public async Task R01_失败文档重试_置pending清error并入队()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(h, DocumentStatus.Failed, error: "解析失败：文件损坏");

        var result = await h.Controller.Reparse(document.Id, CancellationToken.None);

        Assert.Equal(202, F2Harness.StatusOf(result));
        var body = F2Harness.JsonOf(result);
        Assert.Equal(document.Id, body.GetProperty("id").GetString());
        Assert.Equal(DocumentStatus.Pending, body.GetProperty("status").GetString());

        var stored = await h.Repository.GetAsync(document.Id);
        Assert.NotNull(stored);
        Assert.Equal(DocumentStatus.Pending, stored!.Status);
        Assert.Null(stored.Error);
        Assert.Equal(1, h.Queue.Count);
    }

    [Fact]
    public async Task R02_已解析文档重试_同样回到pending()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(
            h, DocumentStatus.Parsed, pageCount: 1, parsedAt: DateTimeOffset.UtcNow.ToString("o"));

        var result = await h.Controller.Reparse(document.Id, CancellationToken.None);

        Assert.Equal(202, F2Harness.StatusOf(result));
        var stored = await h.Repository.GetAsync(document.Id);
        Assert.NotNull(stored);
        Assert.Equal(DocumentStatus.Pending, stored!.Status);
        Assert.Null(stored.Error);
        Assert.Equal(1, h.Queue.Count);
    }

    [Fact]
    public async Task R03_不存在的文档返回404且无副作用()
    {
        using var h = new F2Harness();
        var beforeDocs = (await h.Repository.ListAsync()).Count;
        var beforeQueue = h.Queue.Count;

        var result = await h.Controller.Reparse("not-a-real-id", CancellationToken.None);

        Assert.Equal(404, F2Harness.StatusOf(result));
        Assert.Equal(beforeDocs, (await h.Repository.ListAsync()).Count);
        Assert.Equal(beforeQueue, h.Queue.Count);
        Assert.Empty(await h.Repository.GetPagesAsync("not-a-real-id"));
        Assert.Empty(await h.Repository.GetChunksAsync("not-a-real-id"));
    }

    [Fact]
    public async Task R04_连续重试两次_入队两次且最终仍为pending()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(h, DocumentStatus.Failed, error: "解析失败");

        var first = await h.Controller.Reparse(document.Id, CancellationToken.None);
        var second = await h.Controller.Reparse(document.Id, CancellationToken.None);

        Assert.Equal(202, F2Harness.StatusOf(first));
        Assert.Equal(202, F2Harness.StatusOf(second));
        Assert.Equal(2, h.Queue.Count);

        var stored = await h.Repository.GetAsync(document.Id);
        Assert.NotNull(stored);
        Assert.Equal(DocumentStatus.Pending, stored!.Status);
        Assert.Null(stored.Error);
    }

    [Fact]
    public async Task R05_重试后列表显示pending且无错误()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(h, DocumentStatus.Failed, error: "解析失败");

        await h.Controller.Reparse(document.Id, CancellationToken.None);

        var list = F2Harness.JsonOf(await h.ListAsync());
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        var row = list.EnumerateArray().Single(item => item.GetProperty("id").GetString() == document.Id);
        Assert.Equal(DocumentStatus.Pending, row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("error").ValueKind);
        Assert.Equal(IndexStates.Pending, row.GetProperty("indexState").GetString());
    }

    [Fact]
    public async Task R06_重试不提前删除旧页与片段()
    {
        using var h = new F2Harness();
        var document = await SeedAsync(h, DocumentStatus.Failed, error: "索引失败");
        await SeedContentAsync(h, document.Id);

        var result = await h.Controller.Reparse(document.Id, CancellationToken.None);

        Assert.Equal(202, F2Harness.StatusOf(result));
        var stored = await h.Repository.GetAsync(document.Id);
        Assert.NotNull(stored);
        Assert.Equal(DocumentStatus.Pending, stored!.Status);

        var pages = await h.Repository.GetPagesAsync(document.Id);
        Assert.Single(pages);
        Assert.Equal("失败前的正文", pages[0].RawText);

        var chunks = await h.Repository.GetChunksAsync(document.Id);
        Assert.Single(chunks);
        Assert.Equal("失败前的正文", chunks[0].Content);
        Assert.Equal(1, h.Queue.Count);
    }

    private static async Task<DocumentRecord> SeedAsync(
        F2Harness h, string status, string? error = null, int pageCount = 0, string? parsedAt = null)
    {
        var document = new DocumentRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = $"{status}.md",
            StoredPath = Path.Combine(h.UploadsDir, $"{status}.md"),
            Sha256 = Guid.NewGuid().ToString("N"),
            SizeBytes = 1,
            Status = status,
            PageCount = pageCount,
            Error = error,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            ParsedAt = parsedAt,
        };
        await h.Repository.InsertAsync(document);
        return document;
    }

    private static async Task SeedContentAsync(F2Harness h, string documentId)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        await h.Repository.ReplacePagesAsync(documentId, new[]
        {
            new DocumentPageRecord
            {
                DocumentId = documentId,
                PageNo = 1,
                RawText = "失败前的正文",
                CleanedText = "失败前的正文",
            },
        });
        await h.Repository.ReplaceChunksAsync(documentId, new[]
        {
            new ChunkRecord
            {
                DocumentId = documentId,
                Seq = 0,
                PageFrom = 1,
                PageTo = 1,
                Heading = null,
                Content = "失败前的正文",
                CharCount = 6,
                CreatedAt = now,
            },
        });
    }
}
