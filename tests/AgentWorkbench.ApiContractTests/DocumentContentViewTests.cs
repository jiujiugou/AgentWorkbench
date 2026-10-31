using System.Text.Json;
using AgentWorkbench.ApiContractTests.Support;
using AgentWorkbench.Domain;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentWorkbench.ApiContractTests;

/// <summary>
/// 功能 3「看它的原文和片段」：/pages 与 /chunks 的字段、顺序，
/// 以及「文档不存在」必须与「还没解析完」分开。
/// 用例编号对应 docs/验证与现状/F2/验证方式.md §8（P01–P06）。
/// </summary>
public class DocumentContentViewTests
{
    [Fact]
    public async Task P01_页端点字段完整且按页码升序()
    {
        using var h = new F2Harness();
        var document = await SeedDocumentAsync(h);

        await h.Repository.ReplacePagesAsync(document.Id, new[]
        {
            new DocumentPageRecord
            {
                DocumentId = document.Id,
                PageNo = 2,
                RawText = "第二页   内容",
                CleanedText = "第二页 内容",
            },
            new DocumentPageRecord
            {
                DocumentId = document.Id,
                PageNo = 1,
                RawText = "# 标题\n\n第一段    正文\n\n1",
                CleanedText = "# 标题\n第一段 正文",
            },
        });

        var result = await h.Controller.Pages(document.Id, CancellationToken.None);

        Assert.Equal(200, F2Harness.StatusOf(result));
        var rows = ArrayOf(result);
        Assert.Equal(2, rows.Count);

        Assert.Equal(1, rows[0].GetProperty("pageNo").GetInt32());
        Assert.Equal("# 标题\n\n第一段    正文\n\n1", rows[0].GetProperty("rawText").GetString());
        Assert.Equal("# 标题\n第一段 正文", rows[0].GetProperty("cleanedText").GetString());

        Assert.Equal(2, rows[1].GetProperty("pageNo").GetInt32());
        Assert.Equal("第二页   内容", rows[1].GetProperty("rawText").GetString());
        Assert.Equal("第二页 内容", rows[1].GetProperty("cleanedText").GetString());
    }

    [Fact]
    public async Task P02_片段端点字段完整且按序号升序()
    {
        using var h = new F2Harness();
        var document = await SeedDocumentAsync(h);
        var now = DateTimeOffset.UtcNow.ToString("o");

        await h.Repository.ReplaceChunksAsync(document.Id, new[]
        {
            new ChunkRecord
            {
                DocumentId = document.Id,
                Seq = 2,
                PageFrom = 2,
                PageTo = 2,
                Heading = null,
                Content = "片段二内容",
                CharCount = 5,
                CreatedAt = now,
            },
            new ChunkRecord
            {
                DocumentId = document.Id,
                Seq = 0,
                PageFrom = 1,
                PageTo = 1,
                Heading = "## 小节一",
                Content = "片段零内容",
                CharCount = 5,
                CreatedAt = now,
            },
            new ChunkRecord
            {
                DocumentId = document.Id,
                Seq = 1,
                PageFrom = 1,
                PageTo = 2,
                Heading = null,
                Content = "片段一内容",
                CharCount = 5,
                CreatedAt = now,
            },
        });

        var result = await h.Controller.Chunks(document.Id, CancellationToken.None);

        Assert.Equal(200, F2Harness.StatusOf(result));
        var rows = ArrayOf(result);
        Assert.Equal(3, rows.Count);
        Assert.Equal(0, rows[0].GetProperty("seq").GetInt32());
        Assert.Equal(1, rows[1].GetProperty("seq").GetInt32());
        Assert.Equal(2, rows[2].GetProperty("seq").GetInt32());

        foreach (var row in rows)
        {
            var content = row.GetProperty("content").GetString();
            Assert.False(string.IsNullOrEmpty(content));
            Assert.Equal(content!.Length, row.GetProperty("charCount").GetInt32());
            Assert.InRange(row.GetProperty("pageFrom").GetInt32(), 1, 2);
            Assert.InRange(row.GetProperty("pageTo").GetInt32(), 1, 2);
            Assert.True(row.GetProperty("pageFrom").GetInt32() <= row.GetProperty("pageTo").GetInt32());
        }

        Assert.Equal("## 小节一", rows[0].GetProperty("heading").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("heading").ValueKind);
    }

    [Fact]
    public async Task P03_不存在的文档读页返回404()
    {
        using var h = new F2Harness();

        var result = await h.Controller.Pages("not-a-real-id", CancellationToken.None);

        Assert.Equal(404, F2Harness.StatusOf(result));
    }

    [Fact]
    public async Task P04_不存在的文档读片段返回404()
    {
        using var h = new F2Harness();

        var result = await h.Controller.Chunks("not-a-real-id", CancellationToken.None);

        Assert.Equal(404, F2Harness.StatusOf(result));
    }

    [Fact]
    public async Task P05_还没解析完返回空数组而不是404()
    {
        using var h = new F2Harness();
        var pending = await SeedDocumentAsync(h, DocumentStatus.Pending);
        var parsing = await SeedDocumentAsync(h, DocumentStatus.Parsing);

        foreach (var id in new[] { pending.Id, parsing.Id })
        {
            var pages = await h.Controller.Pages(id, CancellationToken.None);
            Assert.Equal(200, F2Harness.StatusOf(pages));
            Assert.Empty(ArrayOf(pages));

            var chunks = await h.Controller.Chunks(id, CancellationToken.None);
            Assert.Equal(200, F2Harness.StatusOf(chunks));
            Assert.Empty(ArrayOf(chunks));
        }
    }

    [Fact]
    public async Task P06_失败但页块已落库时原文与片段仍可读()
    {
        using var h = new F2Harness();
        var document = await SeedDocumentAsync(h, DocumentStatus.Failed, error: "索引失败（unauthorized）：bad key");
        var now = DateTimeOffset.UtcNow.ToString("o");

        await h.Repository.ReplacePagesAsync(document.Id, new[]
        {
            new DocumentPageRecord
            {
                DocumentId = document.Id,
                PageNo = 1,
                RawText = "失败前的正文",
                CleanedText = "失败前的正文",
            },
        });
        await h.Repository.ReplaceChunksAsync(document.Id, new[]
        {
            new ChunkRecord
            {
                DocumentId = document.Id,
                Seq = 0,
                PageFrom = 1,
                PageTo = 1,
                Heading = null,
                Content = "失败前的正文",
                CharCount = 6,
                CreatedAt = now,
            },
        });

        var pages = await h.Controller.Pages(document.Id, CancellationToken.None);
        Assert.Equal(200, F2Harness.StatusOf(pages));
        var pageRows = ArrayOf(pages);
        Assert.Single(pageRows);
        Assert.Equal("失败前的正文", pageRows[0].GetProperty("rawText").GetString());

        var chunks = await h.Controller.Chunks(document.Id, CancellationToken.None);
        Assert.Equal(200, F2Harness.StatusOf(chunks));
        var chunkRows = ArrayOf(chunks);
        Assert.Single(chunkRows);
        Assert.Equal("失败前的正文", chunkRows[0].GetProperty("content").GetString());
    }

    private static async Task<DocumentRecord> SeedDocumentAsync(
        F2Harness h, string status = DocumentStatus.Parsed, string? error = null)
    {
        var document = new DocumentRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = $"{status}.md",
            StoredPath = Path.Combine(h.UploadsDir, $"{status}.md"),
            Sha256 = Guid.NewGuid().ToString("N"),
            SizeBytes = 1,
            Status = status,
            Error = error,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        await h.Repository.InsertAsync(document);
        return document;
    }

    private static List<JsonElement> ArrayOf(IActionResult result)
    {
        var body = F2Harness.JsonOf(result);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        return body.EnumerateArray().ToList();
    }
}
