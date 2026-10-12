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
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.ModelRuntime")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Conversation")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.ToolExecution")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Safety")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.AgentLoop")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.AgentHarness")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Context")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Memory")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Observability")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Workflow");

        Types().That().ResideInNamespaceMatching("^AgentWorkbench\\.Domain")
            .Should().NotDependOnAny(infrastructure)
            .Check(TestArchitecture.Architecture);
    }

    /// <summary>
    /// 负控：组合根（Webapi）只允许被引用，不允许被任何模块反过来依赖。
    /// 若哪天有人在模块里 using 了 Webapi，本用例必须变红。
    /// </summary>
    [Fact]
    public void Modules_should_not_depend_on_webapi()
    {
        var modules = Types().That()
            .ResideInNamespaceMatching("^AgentWorkbench\\.Shared")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Domain")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Persistence")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Documents")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Knowledge")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Providers")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.ModelRuntime")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Conversation")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.ToolExecution")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Safety")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.AgentLoop")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.AgentHarness")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Context")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Memory")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Observability")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Workflow");

        var webapi = Types().That().ResideInNamespaceMatching("^AgentWorkbench\\.Webapi");

        modules.Should().NotDependOnAny(webapi).Check(TestArchitecture.Architecture);
    }

    /// <summary>
    /// Memory 契约模块必须保持纯净：落库实现放 Memory.Persistence，不能在 Memory 里 using Persistence。
    /// 负控：给 AgentWorkbench.Memory 加一行 <c>using AgentWorkbench.Persistence;</c> → 本用例必须变红。
    /// </summary>
    [Fact]
    public void Memory_should_not_depend_on_persistence()
    {
        var persistence = Types().That().ResideInNamespaceMatching("^AgentWorkbench\\.Persistence");

        Types().That().ResideInNamespaceMatching("^AgentWorkbench\\.Memory$")
            .Should().NotDependOnAny(persistence)
            .Check(TestArchitecture.Architecture);
    }

    /// <summary>
    /// Context 是纯组装面：不查库、不引记忆；长期记忆由编排层映射成中性片段后传入。
    /// 负控：给 Context 加一行 <c>using AgentWorkbench.Memory;</c> 或 <c>using AgentWorkbench.Persistence;</c> → 必须变红。
    /// </summary>
    [Fact]
    public void Context_should_not_depend_on_memory_or_persistence()
    {
        var data = Types().That()
            .ResideInNamespaceMatching("^AgentWorkbench\\.Memory")
            .Or().ResideInNamespaceMatching("^AgentWorkbench\\.Persistence");

        Types().That().ResideInNamespaceMatching("^AgentWorkbench\\.Context$")
            .Should().NotDependOnAny(data)
            .Check(TestArchitecture.Architecture);
    }
}
