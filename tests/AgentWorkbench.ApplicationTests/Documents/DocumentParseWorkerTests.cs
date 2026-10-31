using System.Text;
using AgentWorkbench.ApplicationTests.Support;
using AgentWorkbench.Domain;
using AgentWorkbench.Documents.Parsing;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Knowledge.Abstractions.Embedding;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Documents;

/// <summary>
/// 功能 2 的状态机：DocumentParseWorker 把 pending 推进到 parsed / failed，
/// 失败必须留下可读原因，暂时性索引失败不得伪装成 failed。
/// 用例编号对应 docs/验证与现状/F2/验证方式.md §7（S06–S11）。
/// </summary>
public class DocumentParseWorkerTests
{
    [Fact]
    public async Task S06_文本解析成功_转为parsed并落库页与片段()
    {
        using var h = new ParseWorkerHarness();
        var document = await h.InsertAsync(
            "a.md",
            Encoding.UTF8.GetBytes("这是一个用于解析测试的文档。\n它应该产生至少一个片段。\n"));

        var result = await h.ProcessAsync(document.Id);

        Assert.Equal(DocumentStatus.Parsed, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(1, result.PageCount);
        Assert.NotNull(result.ParsedAt);
        Assert.Single(await h.Repository.GetPagesAsync(document.Id));
        Assert.NotEmpty(await h.Repository.GetChunksAsync(document.Id));
    }

    [Fact]
    public async Task S07_空白文本_转为failed并带原因()
    {
        using var h = new ParseWorkerHarness();
        var document = await h.InsertAsync("blank.md", Encoding.UTF8.GetBytes("   \n\t\n"));

        var result = await h.ProcessAsync(document.Id);

        Assert.Equal(DocumentStatus.Failed, result.Status);
        Assert.Contains("未包含有效文本内容", result.Error);
        Assert.Equal(0, result.PageCount);
        Assert.Null(result.ParsedAt);
        Assert.Empty(await h.Repository.GetChunksAsync(document.Id));
    }

    [Fact]
    public async Task S08_索引永久失败_转为failed且全文仍落库()
    {
        using var h = new ParseWorkerHarness();
        var document = await h.InsertAsync(
            "a.md",
            Encoding.UTF8.GetBytes("索引会永久失败，但正文应该已经写进页与片段。\n"));
        h.Knowledge
            .Setup(k => k.IndexDocumentAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexResult(
                1, 1, 0,
                new EmbeddingFailure(EmbeddingFailureCode.Unauthorized, FailureKind.Permanent, "bad key")));

        var result = await h.ProcessAsync(document.Id);

        Assert.Equal(DocumentStatus.Failed, result.Status);
        Assert.Contains("索引失败", result.Error);
        Assert.Contains(EmbeddingFailureCode.Unauthorized, result.Error);
        Assert.Equal(1, result.PageCount);
        Assert.Single(await h.Repository.GetPagesAsync(document.Id));
        Assert.NotEmpty(await h.Repository.GetChunksAsync(document.Id));
    }

    [Fact]
    public async Task S09_索引暂时失败_保持parsed且不写错误()
    {
        using var h = new ParseWorkerHarness();
        var document = await h.InsertAsync(
            "a.md",
            Encoding.UTF8.GetBytes("索引暂时失败时，全文仍可检索，不应把文档判死。\n"));
        h.Knowledge
            .Setup(k => k.IndexDocumentAsync(document.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexResult(
                1, 1, 0,
                new EmbeddingFailure(EmbeddingFailureCode.Unavailable, FailureKind.Transient, "down")));

        var result = await h.ProcessAsync(document.Id);

        Assert.Equal(DocumentStatus.Parsed, result.Status);
        Assert.Null(result.Error);

        var chunks = await h.Repository.CountChunksAsync(document.Id);
        var vectors = await h.Repository.CountVectorsAsync(document.Id);
        Assert.True(chunks > 0);
        Assert.Equal(0, vectors);
        Assert.Equal(IndexStates.TextOnly, IndexStates.Describe(result.Status, chunks, vectors));
    }

    [Fact]
    public async Task S10_文件缺失_转为failed并说明原因()
    {
        using var h = new ParseWorkerHarness();
        var document = await h.InsertAsync("missing.pdf", content: null, createFile: false);

        var result = await h.ProcessAsync(document.Id);

        Assert.Equal(DocumentStatus.Failed, result.Status);
        Assert.Contains("上传文件不存在", result.Error);
    }

    [Fact]
    public async Task S11_页数超限_转为failed并说明原因()
    {
        using var h = new ParseWorkerHarness(maxPages: 2);
        var document = await h.InsertAsync("a.pdf", [1, 2, 3]);
        h.PdfExtractor
            .Setup(x => x.Extract(document.StoredPath))
            .Returns(new List<ExtractedPage>
            {
                new(1, "第一页"),
                new(2, "第二页"),
                new(3, "第三页"),
            });

        var result = await h.ProcessAsync(document.Id);

        Assert.Equal(DocumentStatus.Failed, result.Status);
        Assert.Contains("超过上限", result.Error);
    }
}
