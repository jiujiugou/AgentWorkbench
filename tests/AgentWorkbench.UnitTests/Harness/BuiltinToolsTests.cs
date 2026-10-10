using AgentWorkbench.AgentHarness;
using AgentWorkbench.AgentHarness.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentWorkbench.UnitTests.Harness;

public sealed class BuiltinToolsTests
{
    private static BuiltinTools CreateTools()
        => new(new ServiceCollection().BuildServiceProvider());

    [Fact]
    public void GetTools_ExposesExpectedNames()
    {
        var names = CreateTools().GetTools().Select(t => t.Name).ToHashSet();

        Assert.Contains("get_current_time", names);
        Assert.Contains("calculator", names);
        Assert.Contains("search_knowledge", names);
        Assert.Contains("list_documents", names);
    }

    [Fact]
    public void DefaultWhitelist_CoversAllBuiltinTools()
    {
        var names = CreateTools().GetTools().Select(t => t.Name).ToHashSet();
        var whitelist = new HarnessOptions().Tools.Whitelist;

        Assert.All(names, name => Assert.Contains(name, whitelist));
    }
}
