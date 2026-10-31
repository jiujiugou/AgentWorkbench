using System.Text.Json;
using AgentWorkbench.ApiContractTests.Support;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using Xunit;

namespace AgentWorkbench.ApiContractTests;

/// <summary>
/// 功能 2「看它处理到哪了」：列表/详情必须带 status / error / indexState，
/// 且只有解析成功的文档才可能被标为可检索。
/// 用例编号对应 docs/验证与现状/F2/验证方式.md §7（S03–S05）。
/// </summary>
public class DocumentStatusVisibilityTests
{
    [Fact]
    public async Task S03_列表三态可见_失败行带原因()
    {
        using var h = new F2Harness();
        var pending = await SeedAsync(h, DocumentStatus.Pending);
        var parsing = await SeedAsync(h, DocumentStatus.Parsing);
        var failed = await SeedAsync(h, DocumentStatus.Failed, error: "解析失败：文件损坏");
        var parsed = await SeedAsync(h, DocumentStatus.Parsed, chunks: 2);

        var rows = await ListRowsAsync(h);

        AssertRow(rows, pending.Id, DocumentStatus.Pending, expectedError: null, IndexStates.Pending);
        AssertRow(rows, parsing.Id, DocumentStatus.Parsing, expectedError: null, IndexStates.Pending);
        AssertRow(rows, failed.Id, DocumentStatus.Failed, expectedError: "解析失败：文件损坏", IndexStates.Failed);
        AssertRow(rows, parsed.Id, DocumentStatus.Parsed, expectedError: null, IndexStates.TextOnly);
    }

    [Fact]
    public async Task S04_单文档详情字段与列表一致_不存在返回404()
    {
        using var h = new F2Harness();
        var failed = await SeedAsync(h, DocumentStatus.Failed, error: "索引失败：鉴权被拒");

        var detail = await h.Controller.Get(failed.Id, CancellationToken.None);

        Assert.Equal(200, F2Harness.StatusOf(detail));
        var body = F2Harness.JsonOf(detail);
        Assert.Equal(failed.Id, body.GetProperty("id").GetString());
        Assert.Equal(DocumentStatus.Failed, body.GetProperty("status").GetString());
        Assert.Equal("索引失败：鉴权被拒", body.GetProperty("error").GetString());
        Assert.Equal(IndexStates.Failed, body.GetProperty("indexState").GetString());

        var missing = await h.Controller.Get("not-a-real-id", CancellationToken.None);
        Assert.Equal(404, F2Harness.StatusOf(missing));
    }

    [Fact]
    public async Task S05_处理中与失败都不可检索_仅解析成功可检索()
    {
        using var h = new F2Harness();
        var pending = await SeedAsync(h, DocumentStatus.Pending);
        var parsing = await SeedAsync(h, DocumentStatus.Parsing);
        var failed = await SeedAsync(h, DocumentStatus.Failed, error: "解析失败");
        var parsed = await SeedAsync(h, DocumentStatus.Parsed, chunks: 2);

        var rows = await ListRowsAsync(h);

        Assert.Equal(IndexStates.Pending, IndexStateOf(rows, pending.Id));
        Assert.Equal(IndexStates.Pending, IndexStateOf(rows, parsing.Id));
        Assert.Equal(IndexStates.Failed, IndexStateOf(rows, failed.Id));
        Assert.Equal(IndexStates.TextOnly, IndexStateOf(rows, parsed.Id));

        foreach (var id in new[] { pending.Id, parsing.Id, failed.Id })
            Assert.False(IsSearchable(IndexStateOf(rows, id)), $"文档 {id} 不应被标为可检索");

        Assert.True(IsSearchable(IndexStateOf(rows, parsed.Id)));
    }

    private static async Task<DocumentRecord> SeedAsync(
        F2Harness h, string status, string? error = null, int chunks = 0)
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

        if (chunks > 0)
        {
            var records = Enumerable.Range(0, chunks)
                .Select(i => new ChunkRecord
                {
                    DocumentId = document.Id,
                    Seq = i,
                    PageFrom = 1,
                    PageTo = 1,
                    Content = $"片段 {i}",
                    CharCount = 4,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
                })
                .ToList();
            await h.Repository.ReplaceChunksAsync(document.Id, records);
        }

        return document;
    }

    private static async Task<List<JsonElement>> ListRowsAsync(F2Harness h)
    {
        var list = F2Harness.JsonOf(await h.ListAsync());
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        return list.EnumerateArray().ToList();
    }

    private static JsonElement RowOf(IReadOnlyList<JsonElement> rows, string id) =>
        rows.Single(row => row.GetProperty("id").GetString() == id);

    private static string? IndexStateOf(IReadOnlyList<JsonElement> rows, string id) =>
        RowOf(rows, id).GetProperty("indexState").GetString();

    private static void AssertRow(
        IReadOnlyList<JsonElement> rows, string id, string expectedStatus, string? expectedError, string expectedIndexState)
    {
        var row = RowOf(rows, id);
        Assert.Equal(expectedStatus, row.GetProperty("status").GetString());
        Assert.Equal(expectedIndexState, row.GetProperty("indexState").GetString());

        var error = row.GetProperty("error");
        if (expectedError is null)
            Assert.Equal(JsonValueKind.Null, error.ValueKind);
        else
            Assert.Equal(expectedError, error.GetString());
    }

    private static bool IsSearchable(string? state) =>
        state is IndexStates.Full or IndexStates.TextOnly or IndexStates.Partial;
}
