using AgentWorkbench.Review;
using Xunit;

namespace AgentWorkbench.UnitTests.Review;

public sealed class ReviewProgressNotifierTests
{
    [Fact]
    public async Task NotifyAsync_CompletesWithoutThrowing()
    {
        var notifier = new NullReviewProgressNotifier();

        await notifier.NotifyAsync(1, "running", "[]", CancellationToken.None);
    }
}
