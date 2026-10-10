using AgentWorkbench.Review.Comparison;
using Xunit;

namespace AgentWorkbench.UnitTests.Review.Comparison;

public sealed class DocumentRelevanceTests
{
    [Fact]
    public void LooksLikeContract_ContractText_True()
    {
        Assert.True(DocumentRelevance.LooksLikeContract("出卖人（甲方）：华成精密制造有限公司"));
    }

    [Fact]
    public void LooksLikeContract_UnrelatedNotice_False()
    {
        Assert.False(DocumentRelevance.LooksLikeContract("市图书馆开馆时间调整通知：一楼综合阅览区临时调整开放时间。"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksLikeContract_Blank_False(string? text)
    {
        Assert.False(DocumentRelevance.LooksLikeContract(text));
    }
}
