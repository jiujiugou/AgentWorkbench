using AgentWorkbench.ModelRuntime;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.UnitTests.Conversation;

/// <summary>透传模型运行时：对话单测只关心消息/工具装配，不叠加超时重试。</summary>
internal sealed class PassthroughModelRuntime : IModelRuntime
{
    public IChatClient Wrap(IChatClient innerClient) => innerClient;

    public ModelUsageSnapshot GetUsage() => new(0, 0, 0);
}

/// <summary>固定工具目录：由测试直接决定「当前允许哪些工具」。</summary>
internal sealed class FakeToolCatalog(IReadOnlyList<AITool> tools) : IToolCatalog
{
    public FakeToolCatalog() : this([])
    {
    }

    public IReadOnlyList<AITool> GetTools() => tools;

    public IReadOnlyList<string> GetToolNames() => tools.Select(tool => tool.Name).ToList();
}
