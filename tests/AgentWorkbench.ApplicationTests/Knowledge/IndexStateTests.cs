using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Knowledge;

/// <summary>
/// 功能 2 的状态可见性：indexState 的映射与「可检索」集合。
/// 用例编号对应 docs/验证与现状/F2/验证方式.md §7（S01–S02）。
/// </summary>
public class IndexStateTests
{
    [Theory]
    [InlineData(DocumentStatus.Pending, 0, 0, IndexStates.Pending)]
    [InlineData(DocumentStatus.Pending, 3, 3, IndexStates.Pending)]
    [InlineData(DocumentStatus.Parsing, 0, 0, IndexStates.Pending)]
    [InlineData(DocumentStatus.Parsing, 3, 3, IndexStates.Pending)]
    [InlineData(DocumentStatus.Failed, 0, 0, IndexStates.Failed)]
    [InlineData(DocumentStatus.Failed, 3, 3, IndexStates.Failed)]
    [InlineData(DocumentStatus.Parsed, 0, 0, IndexStates.Empty)]
    [InlineData(DocumentStatus.Parsed, 3, 0, IndexStates.TextOnly)]
    [InlineData(DocumentStatus.Parsed, 3, 1, IndexStates.Partial)]
    [InlineData(DocumentStatus.Parsed, 3, 3, IndexStates.Full)]
    [InlineData(DocumentStatus.Parsed, 3, 4, IndexStates.Full)]
    public void S01_indexState映射与状态机一致(
        string status, int chunkCount, int vectorCount, string expected)
    {
        Assert.Equal(expected, IndexStates.Describe(status, chunkCount, vectorCount));
    }

    [Theory]
    [InlineData(DocumentStatus.Pending, 0, 0, false)]
    [InlineData(DocumentStatus.Parsing, 3, 3, false)]
    [InlineData(DocumentStatus.Failed, 3, 3, false)]
    [InlineData(DocumentStatus.Parsed, 0, 0, false)]
    [InlineData(DocumentStatus.Parsed, 3, 0, true)]
    [InlineData(DocumentStatus.Parsed, 3, 1, true)]
    [InlineData(DocumentStatus.Parsed, 3, 3, true)]
    public void S02_只有成功的文档才可能被标为可检索(
        string status, int chunkCount, int vectorCount, bool expected)
    {
        var state = IndexStates.Describe(status, chunkCount, vectorCount);

        Assert.Equal(expected, IsSearchable(state));
    }

    private static bool IsSearchable(string state) =>
        state is IndexStates.Full or IndexStates.TextOnly or IndexStates.Partial;
}
