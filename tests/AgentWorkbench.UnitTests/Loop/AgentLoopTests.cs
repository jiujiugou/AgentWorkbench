using AgentWorkbench.AgentLoop;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Loop;

public sealed class AgentLoopTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsActionResult()
    {
        var loop = Build(timeoutSeconds: 5, maxIterations: 3);

        var result = await loop.ExecuteAsync(_ => Task.FromResult(42));

        Assert.Equal(42, result);
        Assert.Equal(3, loop.MaxIterations);
        Assert.Equal(5, loop.TimeoutSeconds);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsRunTimeout_WhenActionExceedsBudget()
    {
        var loop = Build(timeoutSeconds: 1, maxIterations: 3);

        var ex = await Assert.ThrowsAsync<AgentRunTimeoutException>(
            () => loop.ExecuteAsync(async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return 0;
            }));

        Assert.Equal(1, ex.TimeoutSeconds);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCallerCancellation()
    {
        var loop = Build(timeoutSeconds: 30, maxIterations: 3);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => loop.ExecuteAsync(async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return 0;
            }, cts.Token));
    }

    private static AgentLoopService Build(int timeoutSeconds, int maxIterations)
        => new(
            Options.Create(new AgentLoopOptions { TimeoutSeconds = timeoutSeconds, MaxIterations = maxIterations }),
            NullLogger<AgentLoopService>.Instance);
}
