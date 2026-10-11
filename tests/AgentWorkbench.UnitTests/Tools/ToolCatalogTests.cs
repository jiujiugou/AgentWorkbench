using AgentWorkbench.Safety;
using AgentWorkbench.ToolExecution;
using AgentWorkbench.ToolExecution.Builtin;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.UnitTests.Tools;

public sealed class ToolCatalogTests
{
    [Fact]
    public void GetTools_OnlyExposesWhitelistedTools()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IAgentToolProvider, GetCurrentTimeTool>()
            .AddSingleton<IAgentToolProvider, CalculatorTool>()
            .BuildServiceProvider();

        var catalog = BuildCatalog(
            provider,
            new ToolPolicyOptions { Whitelist = [GetCurrentTimeTool.ToolName] });

        Assert.Equal(new[] { GetCurrentTimeTool.ToolName }, catalog.GetToolNames());
    }

    [Fact]
    public void GetTools_DeduplicatesByName()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IAgentToolProvider>(new FakeToolProvider("dup"))
            .AddSingleton<IAgentToolProvider>(new FakeToolProvider("dup"))
            .BuildServiceProvider();

        var catalog = BuildCatalog(provider, new ToolPolicyOptions { Whitelist = ["dup"] });

        Assert.Single(catalog.GetTools());
    }

    [Fact]
    public void GetTools_EmptyWhitelist_ExposesNothing()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IAgentToolProvider, CalculatorTool>()
            .BuildServiceProvider();

        var catalog = BuildCatalog(provider, new ToolPolicyOptions { Whitelist = [] });

        Assert.Empty(catalog.GetTools());
    }

    private static ToolCatalog BuildCatalog(IServiceProvider provider, ToolPolicyOptions policy)
        => new(provider, new ToolPolicy(Options.Create(policy)), NullLogger<ToolCatalog>.Instance);

    private sealed class FakeToolProvider(string name) : IAgentToolProvider
    {
        public IReadOnlyList<AITool> GetTools() => [AIFunctionFactory.Create(() => name, name)];
    }
}
