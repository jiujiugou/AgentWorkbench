namespace AgentWorkbench.ApplicationTests.ActionGateway;

using AgentWorkbench.Tools.Mcp;
using Xunit;

/// <summary>
/// 工具调用超时的可测核心：超时 vs 外部取消必须分开。
/// 负控：把 ToolCallTimeout 的两个 catch 顺序/条件改错，本文件会红。
/// </summary>
public sealed class ToolCallTimeoutTests
{
    [Fact]
    public async Task 超时抛TimeoutException()
    {
        async ValueTask<string> NeverFinishes(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "never";
        }

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await ToolCallTimeout.RunAsync(NeverFinishes, TimeSpan.FromMilliseconds(30), CancellationToken.None));
    }

    [Fact]
    public async Task 外部取消原样抛而非超时()
    {
        using var cts = new CancellationTokenSource();

        async ValueTask<string> NeverFinishes(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "never";
        }

        cts.CancelAfter(20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ToolCallTimeout.RunAsync(NeverFinishes, TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task 正常完成返回值不被超时打断()
    {
        async ValueTask<string> Quick(CancellationToken ct)
        {
            await Task.Delay(5, ct);
            return "ok";
        }

        var result = await ToolCallTimeout.RunAsync(Quick, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task 超时异常被分类器认成传输故障()
        => Assert.True(ToolFailureClassifier.IsTransportFailure(
            new TimeoutException("外部调用超时（0.03s）"), ""));
}
