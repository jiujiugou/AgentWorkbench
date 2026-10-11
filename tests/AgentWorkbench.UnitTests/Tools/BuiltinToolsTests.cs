using AgentWorkbench.Documents.Tools;
using AgentWorkbench.Knowledge.Local.Tools;
using AgentWorkbench.Safety;
using AgentWorkbench.ToolExecution.Builtin;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentWorkbench.UnitTests.Tools;

/// <summary>内置工具分散在各自功能模块里（通用→ToolExecution，知识→Knowledge.Local，文档→Documents），这里做名称与白名单的对齐校验。</summary>
public sealed class BuiltinToolsTests
{
    private static IServiceProvider EmptyServices() => new ServiceCollection().BuildServiceProvider();

    private static List<string> AllBuiltinToolNames()
    {
        var services = EmptyServices();
        var names = new List<string>();
        names.AddRange(new GetCurrentTimeTool().GetTools().Select(t => t.Name));
        names.AddRange(new CalculatorTool().GetTools().Select(t => t.Name));
        names.AddRange(new SearchKnowledgeTool(services).GetTools().Select(t => t.Name));
        names.AddRange(new ListDocumentsTool(services).GetTools().Select(t => t.Name));
        return names;
    }

    [Fact]
    public void GetTools_ExposesExpectedNames()
    {
        var names = AllBuiltinToolNames().ToHashSet();

        Assert.Contains(GetCurrentTimeTool.ToolName, names);
        Assert.Contains(CalculatorTool.ToolName, names);
        Assert.Contains(SearchKnowledgeTool.ToolName, names);
        Assert.Contains(ListDocumentsTool.ToolName, names);
    }

    [Fact]
    public void DefaultWhitelist_CoversAllBuiltinTools()
    {
        var whitelist = new ToolPolicyOptions().Whitelist;

        Assert.All(AllBuiltinToolNames(), name => Assert.Contains(name, whitelist));
    }
}
