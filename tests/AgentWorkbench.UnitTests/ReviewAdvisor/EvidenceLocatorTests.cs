using AgentWorkbench.Domain;
using AgentWorkbench.ReviewAdvisor.Evidence;
using Xunit;

namespace AgentWorkbench.UnitTests.ReviewAdvisor;

public sealed class EvidenceLocatorTests
{
    private static readonly List<ChunkRecord> Chunks =
    [
        new() { Id = 3, DocumentId = "d", Seq = 0, PageFrom = 1, PageTo = 1, Content = "任何一方违约，应向对方支付合同总价款 10% 的违约金。" }
    ];

    [Fact]
    public void IsGrounded_EvidenceInChunk_True()
    {
        Assert.True(EvidenceLocator.IsGrounded(Chunks, null, 0, "违约金"));
    }

    [Fact]
    public void IsGrounded_EvidenceNotInChunk_False()
    {
        Assert.False(EvidenceLocator.IsGrounded(Chunks, null, 0, "不存在的片段"));
    }

    [Fact]
    public void IsGrounded_UnknownSeq_False()
    {
        Assert.False(EvidenceLocator.IsGrounded(Chunks, null, 99, "违约金"));
    }

    [Fact]
    public void Locate_ReturnsHitWithEvidence()
    {
        var found = EvidenceLocator.Locate(Chunks, "违约");
        var hit = Assert.Single(found);
        Assert.Equal(0, hit.Chunk.Seq);
        Assert.Contains("违约", hit.Evidence);
    }
}
