using ArchUnitNET.xUnit;
using Xunit;

namespace AgentWorkbench.ArchitectureTests;

/// <summary>顶层模块之间不得出现循环依赖；切片必须覆盖所有预期模块。</summary>
public class ModuleCycleTests
{
    [Fact]
    public void Top_level_modules_should_be_free_of_cycles()
    {
        ModuleSlices.Given().Should().BeFreeOfCycles().Check(TestArchitecture.Architecture);
    }

    [Fact]
    public void Slice_assignment_should_cover_the_expected_modules()
    {
        var modules = ModuleSlices
            .Given()
            .GetObjects(TestArchitecture.Architecture)
            .Select(slice => slice.Description)
            .ToHashSet();

        var expected = new[]
        {
            "Shared",
            "Domain",
            "Persistence",
            "Documents",
            "Knowledge",
            "Providers",
            "Agent",
            "Review",
            "ReviewAdvisor",
            "AgentHarness",
            "Workflow",
            "Webapi",
        };
        var missing = expected.Where(module => !modules.Contains(module)).ToList();

        Assert.True(
            missing.Count == 0 && modules.Count >= expected.Length,
            $"modules({modules.Count}): {string.Join(", ", modules)} | missing: {string.Join(", ", missing)}"
        );
    }
}
