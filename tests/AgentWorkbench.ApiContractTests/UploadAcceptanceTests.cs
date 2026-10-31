using System.Security.Cryptography;
using System.Text.Json;
using AgentWorkbench.ApiContractTests.Support;
using AgentWorkbench.Domain;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentWorkbench.ApiContractTests;

/// <summary>
/// 功能 1「传一份文件」的接收契约：阶段1-F2 §1 第 1 件 / §1.1。
/// 拒收路径由 <see cref="UploadValidationTests"/> 守；这里补成功、边界、判定顺序与文件名安全。
/// </summary>
/// <remarks>
/// 用例编号与 docs/验证与现状/F2/验证方式.md 一一对应。
/// 所有断言都落在「响应 + 磁盘 + 库 + 队列」四者上，避免只看 202 就判成功。
/// </remarks>
public class UploadAcceptanceTests
{
    private const long SmallLimit = 1024;

    [Fact]
    public async Task U01_上传PDF_返回处理中并落盘入库入队()
    {
        using var h = new F2Harness();
        var content = Bytes(64);

        var result = await h.UploadAsync("a.pdf", content);

        Assert.Equal(202, F2Harness.StatusOf(result));

        var body = F2Harness.JsonOf(result);
        var id = body.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal("a.pdf", body.GetProperty("fileName").GetString());
        Assert.Equal(DocumentStatus.Pending, body.GetProperty("status").GetString());
        Assert.Equal(Sha256(content), body.GetProperty("sha256").GetString());
        Assert.Equal((long)content.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
        Assert.True(DateTimeOffset.TryParse(body.GetProperty("createdAt").GetString(), out _));

        var record = await h.Repository.GetAsync(id!);
        Assert.NotNull(record);
        Assert.Equal(DocumentStatus.Pending, record!.Status);
        Assert.Equal(1, h.Queue.Count);

        var stored = Assert.Single(h.StoredFiles());
        Assert.Equal(Path.Combine(h.UploadsDir, Sha256(content) + ".pdf"), stored);

        var list = F2Harness.JsonOf(await h.ListAsync());
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        var rows = list.EnumerateArray().ToList();
        var row = Assert.Single(rows);
        Assert.Equal(id, row.GetProperty("id").GetString());
        Assert.Equal(DocumentStatus.Pending, row.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".json")]
    public async Task U02_四种支持格式均可接收且保留扩展名(string extension)
    {
        using var h = new F2Harness();
        var content = Bytes(32);

        var result = await h.UploadAsync("a" + extension, content);

        Assert.Equal(202, F2Harness.StatusOf(result));
        var stored = Assert.Single(h.StoredFiles());
        Assert.EndsWith(extension, stored, StringComparison.Ordinal);
        Assert.Equal(1, h.Queue.Count);
        Assert.Single(await h.Repository.ListAsync());
    }

    [Fact]
    public async Task U03_扩展名大写_接收并统一小写落盘()
    {
        using var h = new F2Harness();
        var content = Bytes(32);

        var result = await h.UploadAsync("A.PDF", content);

        Assert.Equal(202, F2Harness.StatusOf(result));
        Assert.EndsWith(".pdf", Assert.Single(h.StoredFiles()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task U04_恰好等于上限_接收()
    {
        using var h = new F2Harness(maxFileBytes: SmallLimit);
        var content = Bytes((int)SmallLimit);

        var result = await h.UploadAsync("a.md", content);

        Assert.Equal(202, F2Harness.StatusOf(result));
        Assert.Equal((long)SmallLimit, F2Harness.JsonOf(result).GetProperty("sizeBytes").GetInt64());
        Assert.Single(h.StoredFiles());
        Assert.Equal(1, h.Queue.Count);
    }

    [Fact]
    public async Task U05_超过上限一字节_拒收且盘库队都不动()
    {
        using var h = new F2Harness(maxFileBytes: SmallLimit);
        var before = await h.SnapshotAsync();

        var result = await h.UploadAsync("a.md", Bytes((int)SmallLimit + 1));
        var after = await h.SnapshotAsync();

        Assert.Equal(400, F2Harness.StatusOf(result));
        Assert.Equal("FILE_TOO_LARGE", F2Harness.CodeOf(result));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task U06_空文件同时类型不支持_先报文件为空()
    {
        using var h = new F2Harness();
        var before = await h.SnapshotAsync();

        var result = await h.UploadAsync("a.docx", []);
        var after = await h.SnapshotAsync();

        Assert.Equal(400, F2Harness.StatusOf(result));
        Assert.Equal("FILE_EMPTY", F2Harness.CodeOf(result));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task U07_超限同时类型不支持_先报类型不支持()
    {
        using var h = new F2Harness(maxFileBytes: SmallLimit);
        var before = await h.SnapshotAsync();

        var result = await h.UploadAsync("a.docx", Bytes((int)SmallLimit + 1));
        var after = await h.SnapshotAsync();

        Assert.Equal(400, F2Harness.StatusOf(result));
        Assert.Equal("FILE_TYPE_UNSUPPORTED", F2Harness.CodeOf(result));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task U08_无扩展名_按类型不支持拒收()
    {
        using var h = new F2Harness();
        var before = await h.SnapshotAsync();

        var result = await h.UploadAsync("README", Bytes(32));
        var after = await h.SnapshotAsync();

        Assert.Equal(400, F2Harness.StatusOf(result));
        Assert.Equal("FILE_TYPE_UNSUPPORTED", F2Harness.CodeOf(result));
        Assert.Equal(before, after);
    }

    /// <summary>当前行为锁定：上传同一内容两次产生两条记录，但磁盘只保留一份（不做去重）。</summary>
    [Fact]
    public async Task U09_同内容两次_两条记录共用一个磁盘文件()
    {
        using var h = new F2Harness();
        var content = Bytes(32);

        var first = await h.UploadAsync("a.md", content);
        var second = await h.UploadAsync("a.md", content);

        Assert.Equal(202, F2Harness.StatusOf(first));
        Assert.Equal(202, F2Harness.StatusOf(second));

        var firstId = F2Harness.StringOf(first, "id");
        var secondId = F2Harness.StringOf(second, "id");
        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, (await h.Repository.ListAsync()).Count);
        Assert.Single(h.StoredFiles());
        Assert.Equal(2, h.Queue.Count);
    }

    [Fact]
    public async Task U10_路径型文件名_不逃逸上传目录()
    {
        using var h = new F2Harness();
        var content = Bytes(32);

        var result = await h.UploadAsync("../../evil.pdf", content);

        Assert.Equal(202, F2Harness.StatusOf(result));

        var stored = Assert.Single(h.StoredFiles());
        var uploadsRoot = Path.GetFullPath(h.UploadsDir);
        Assert.StartsWith(
            uploadsRoot + Path.DirectorySeparatorChar,
            Path.GetFullPath(stored),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Sha256(content) + ".pdf", Path.GetFileName(stored));
    }

    private static byte[] Bytes(int n)
    {
        var data = new byte[n];
        for (var i = 0; i < n; i++) data[i] = (byte)('a' + i % 26);
        return data;
    }

    private static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
