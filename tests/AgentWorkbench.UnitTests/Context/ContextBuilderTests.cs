using AgentWorkbench.Context;
using AgentWorkbench.Knowledge.Abstractions.Retrieval;
using Xunit;

namespace AgentWorkbench.UnitTests.Context;

public sealed class ContextBuilderTests
{
    private readonly ContextBuilder _builder = new();

    [Fact]
    public void Compose_NoHits_ReturnsInputUnchanged()
        => Assert.Equal("问题", _builder.Compose("问题", []));

    [Fact]
    public void Compose_WithHits_PrependsSourcedFragments()
    {
        var hit = new RetrievalHit
        {
            FileName = "a.pdf",
            Seq = 2,
            PageFrom = 3,
            PageTo = 4,
            Content = "片段内容"
        };

        var composed = _builder.Compose("问题", [hit]);

        Assert.StartsWith("【知识库检索片段】", composed);
        Assert.Contains("《a.pdf》片段#2 (p3-4) :: 片段内容", composed);
        Assert.EndsWith("问题", composed);
    }
}
