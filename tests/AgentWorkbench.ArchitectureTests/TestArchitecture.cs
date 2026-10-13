using ArchUnitNET.Domain;
using ArchUnitNET.Loader;

namespace AgentWorkbench.ArchitectureTests;

/// <summary>加载全部模块程序集，供 ArchUnitNET 规则检查。</summary>
internal static class TestArchitecture
{
    private static readonly string[] AssemblyNames =
    [
        "AgentWorkbench.Shared",
        "AgentWorkbench.Domain",
        "AgentWorkbench.Persistence",
        "AgentWorkbench.Documents",
        "AgentWorkbench.Knowledge.Abstractions",
        "AgentWorkbench.Knowledge.Local",
        "AgentWorkbench.Knowledge",
        "AgentWorkbench.Providers",
        "AgentWorkbench.ModelRuntime",
        "AgentWorkbench.Conversation",
        "AgentWorkbench.ToolExecution",
        "AgentWorkbench.AgentLoop",
        "AgentWorkbench.Memory",
        "AgentWorkbench.Memory.Persistence",
        "AgentWorkbench.Observability",
        "AgentWorkbench.AgentHarness",
        "AgentWorkbench.Workflow",
        "AgentWorkbench.Webapi"
    ];

    public static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(AssemblyNames.Select(name => System.Reflection.Assembly.Load(name)).ToArray())
        .Build();
}
