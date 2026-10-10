using ArchUnitNET.xUnit;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace AgentWorkbench.ArchitectureTests;

/// <summary>分层约束：Domain 必须保持纯净，不得依赖任何基础设施/实现模块。</summary>
public class LayeringTests
{
    [Fact]
    public void Domain_should_not_depend_on_infrastructure()
    {
        var infrastructure = Types().That()
            .ResideInNamespaceMatching("^AgentWorkbench\\.Persistence")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Webapi")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Documents")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Knowledge")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Providers")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Agent")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.AgentHarness")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Workflow");

        Types().That().ResideInNamespaceMatching("^AgentWorkbench\\.Domain")
            .Should().NotDependOnAny(infrastructure)
            .Check(TestArchitecture.Architecture);
    }
}
