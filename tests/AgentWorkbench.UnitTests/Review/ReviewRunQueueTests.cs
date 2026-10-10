using AgentWorkbench.Review;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentWorkbench.UnitTests.Review;

public sealed class ReviewRunQueueTests
{
    [Fact]
    public async Task EnqueueThenRead_PreservesFifoOrder()
    {
        var queue = new ReviewRunQueue(NullLogger<ReviewRunQueue>.Instance);
        await queue.EnqueueAsync(1);
        await queue.EnqueueAsync(2);
        await queue.EnqueueAsync(3);

        var read = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var id in queue.ReadAllAsync(cts.Token))
        {
            read.Add(id);
            if (read.Count == 3)
                break;
        }

        Assert.Equal(new long[] { 1, 2, 3 }, read);
    }

    [Fact]
    public async Task ReadAll_WhenCancelled_StopsEnumeration()
    {
        var queue = new ReviewRunQueue(NullLogger<ReviewRunQueue>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in queue.ReadAllAsync(cts.Token))
                count++;
        });

        Assert.Equal(0, count);
    }
}
