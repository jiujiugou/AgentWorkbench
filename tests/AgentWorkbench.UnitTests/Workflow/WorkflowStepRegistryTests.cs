using AgentWorkbench.Workflow;
using AgentWorkbench.Workflow.Steps;
using Xunit;

namespace AgentWorkbench.UnitTests.Workflow;

public sealed class WorkflowStepRegistryTests
{
    [Fact]
    public void Get_ReturnsExecutorByType()
    {
        var executor = new FakeExecutor("extract");
        var registry = new WorkflowStepRegistry([executor, new FakeExecutor("agent")]);

        Assert.Same(executor, registry.Get("extract"));
        Assert.Equal(new[] { "agent", "extract" }, registry.Types);
    }

    [Fact]
    public void Get_UnknownType_Throws()
    {
        var registry = new WorkflowStepRegistry([new FakeExecutor("extract")]);
        Assert.Throws<InvalidOperationException>(() => registry.Get("nope"));
    }

    private sealed class FakeExecutor(string type) : IWorkflowStepExecutor
    {
        public string Type { get; } = type;

        public Task<string> ExecuteAsync(WorkflowStepContext context, CancellationToken cancellationToken)
            => Task.FromResult("{}");
    }
}
