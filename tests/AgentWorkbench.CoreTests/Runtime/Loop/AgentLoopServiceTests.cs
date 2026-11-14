using AgentWorkbench.Runtime.Loop;
using AgentWorkbench.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.CoreTests.Runtime.Loop;

/// <summary>
/// F1.2 / F1.5 / F1.6：run 级墙钟、取消与超时的区分、预算属性透出。
/// AgentLoopService 只依赖 IOptions + ILogger，可直接实例化。
/// </summary>
public sealed class AgentLoopServiceTests
{
    private static AgentLoopService Loop(int timeoutSeconds = 1, int maxIterations = 6)
        => new(Options.Create(new AgentLoopOptions
        {
            TimeoutSeconds = timeoutSeconds,
            MaxIterations = maxIterations
        }), NullLogger<AgentLoopService>.Instance);

    [Fact]
    public async Task 整轮超时_抛_AgentRunTimeoutException()
    {
        var loop = Loop(timeoutSeconds: 1);

        var ex = await Assert.ThrowsAsync<AgentRunTimeoutException>(() =>
            loop.ExecuteAsync(async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return 42;
            }));

        Assert.Equal(1, ex.TimeoutSeconds);
        // 负控 [F1.2]：删掉 ExecuteAsync 的 catch、让 OperationCanceledException 原样冒泡 → 本条必须红。
    }

    [Fact]
    public async Task 正常完成_原样返回结果()
    {
        var loop = Loop(timeoutSeconds: 30);

        var result = await loop.ExecuteAsync(_ => Task.FromResult(42));

        Assert.Equal(42, result);
        // 对照组 [F1.2]：证明超时测试不是「ExecuteAsync 永远抛」。
    }

    [Fact]
    public async Task 调用方取消_不得被归类成超时()
    {
        var loop = Loop(timeoutSeconds: 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            loop.ExecuteAsync(ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(42);
            }, cts.Token));

        Assert.IsNotType<AgentRunTimeoutException>(ex);
        // 负控 [F1.6]：去掉 catch 的 `when (!cancellationToken.IsCancellationRequested)`
        // → 取消被吞成 AgentRunTimeoutException，本条必须红。
    }

    [Fact]
    public void 预算属性从配置透出_且每次_create_独立守卫()
    {
        var loop = Loop(timeoutSeconds: 7, maxIterations: 3);

        Assert.Equal(3, loop.MaxIterations);
        Assert.Equal(7, loop.TimeoutSeconds);
        var a = loop.CreateGuard();
        var b = loop.CreateGuard(new Mock<IAgentUsageSource>().Object);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
        // 负控 [F1.4]：让 CreateGuard 返回同一个静态实例 → 本条必须红。
    }

    [Fact]
    public void 两种超时_归类码不能合并()
    {
        Assert.NotEqual(AgentRunCode.RunTimeout, AgentRunCode.ModelTimeout);
        Assert.Equal("timeout", AgentRunStatus.Timeout);
        // 负控 [F1.5]：把两个 code 写成同一字符串 → 本条必须红。
    }
}
