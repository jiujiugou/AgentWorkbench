using ArchUnitNET.Domain;
using ArchUnitNET.Fluent.Slices;

namespace AgentWorkbench.ArchitectureTests;

/// <summary>按 `AgentWorkbench.&lt;Module&gt;` 顶层命名空间切分为模块切片。</summary>
internal static class ModuleSlices
{
    private const string RootPrefix = "AgentWorkbench.";

    private static readonly SliceAssignment Assignment = new(
        SliceIdentifierFor,
        "AgentWorkbench.<Module> 顶层模块"
    );

    public static GivenSlices Given()
    {
        var creator = new SliceRuleCreator();
        creator.SetSliceAssignment(Assignment);
        return new GivenSlices(creator);
    }

    private static SliceIdentifier SliceIdentifierFor(IType type)
    {
        var ns = type.Namespace.FullName;
        if (string.IsNullOrEmpty(ns) || !ns.StartsWith(RootPrefix, StringComparison.Ordinal))
            return SliceIdentifier.Ignore();

        var module = ns.Substring(RootPrefix.Length).Split('.')[0];
        return module.Length == 0 ? SliceIdentifier.Ignore() : SliceIdentifier.Of(module);
    }
}
