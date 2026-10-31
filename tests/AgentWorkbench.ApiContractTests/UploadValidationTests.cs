using AgentWorkbench.ApiContractTests.Support;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentWorkbench.ApiContractTests;

/// <summary>
/// 上传校验（契约 §1.3 / §1.5 / 验收 A4–A6、NO_FILE、FILE_EMPTY）。
/// 一条断言三件事：状态码、业务码、以及「盘上/库里/队列里什么都没动」。
/// </summary>
/// <remarks>
/// 第三条是重点：它验证契约 §1.2 的顺序约束「校验必须先于写磁盘」。
/// 顺序写反时前两条照样过，只有第三条会红。
/// </remarks>
public class UploadValidationTests
{
    private const long Limit = 30L * 1024 * 1024;

    public static TheoryData<string, int, int, string> Cases => new()
    {
        { "不带 file 字段", -1, 400, "NO_FILE" },
        { "空文件", 0, 400, "FILE_EMPTY" },
        { "扩展名不支持（.docx）", 64, 400, "FILE_TYPE_UNSUPPORTED" },
        { "超业务上限", (int)Limit + 1, 400, "FILE_TOO_LARGE" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A4_校验失败_给业务码且盘库队都没动(
        string scenario, int contentBytes, int expectedStatus, string expectedCode)
    {
        // 内容在这里生成，不放进 Cases：xunit 发现阶段会对 MemberData 求值并做
        // 「参数是否可序列化」的检查，30 MB 的 byte[] 会被逐元素走一遍，导致发现阶段假死。
        var content = contentBytes < 0 ? null : Bytes(contentBytes);
        using var h = new F2Harness(maxFileBytes: Limit);
        var before = await h.SnapshotAsync();

        var fileName = scenario.Contains("docx") ? "a.docx" : "a.md";

        var result = await h.UploadAsync(fileName, content);

        var after = await h.SnapshotAsync();
        var actualStatus = F2Harness.StatusOf(result);
        var actualCode = F2Harness.CodeOf(result);

        Assert.True(
            actualStatus == expectedStatus && actualCode == expectedCode
                && after.Files == before.Files && after.Docs == before.Docs && after.Queued == before.Queued,
            $"场景「{scenario}」不符合契约：\n"
            + $"  状态码  期望 {expectedStatus}，实际 {actualStatus}\n"
            + $"  业务码  期望 {expectedCode}，实际 {actualCode ?? "（响应体里没有 code 字段）"}\n"
            + $"  磁盘    期望不变（{before.Files}），实际 {after.Files}\n"
            + $"  库记录  期望不变（{before.Docs}），实际 {after.Docs}\n"
            + $"  队列    期望不变（{before.Queued}），实际 {after.Queued}");
    }

    private static byte[] Bytes(int n)
    {
        var data = new byte[n];
        for (var i = 0; i < n; i++) data[i] = (byte)('a' + i % 26);
        return data;
    }
}
