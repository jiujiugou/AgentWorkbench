using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Tools.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ToolExecution;

/// <summary>
/// F2.1 工具目录：只暴露白名单内工具、按名去重。
/// 白名单外的工具是「模型根本看不到」——连名字都不进 GetToolNames()/GetTools()。
/// </summary>
public class ToolCatalogTests
{
    private static AITool Tool(string name)
        => AIFunctionFactory.Create(() => "x", name, "测试工具");

    /// <summary>按给定白名单和若干「工具提供方」装配一个真实 ToolCatalog。</summary>
    private static ToolCatalog CreateCatalog(
        IReadOnlyList<string> whitelist,
        params IReadOnlyList<AITool>[] providerGroups)
    {
        var services = new ServiceCollection();
        foreach (var group in providerGroups)
        {
            var provider = new Mock<IAgentToolProvider>();
            provider.Setup(p => p.GetTools()).Returns(group.ToList());
            services.AddSingleton<IAgentToolProvider>(provider.Object);
        }

        var policy = new Mock<IToolPolicy>();
        policy.SetupGet(p => p.Whitelist).Returns(whitelist.ToList());
        policy.Setup(p => p.IsAllowed(It.IsAny<string>()))
              .Returns<string>(name => whitelist.Contains(name, StringComparer.Ordinal));

        return new ToolCatalog(
            services.BuildServiceProvider(),
            policy.Object,
            NullLogger<ToolCatalog>.Instance);
    }

    // [F2.1] 白名单外工具完全不可见（该拒的），白名单内工具都在（该放行的）。
    // 负控：删掉 ToolCatalog.cs:38 的 .Where(tool => _policy.IsAllowed(tool.Name))，
    //       GetToolNames() 会变成 4 个且含 blocked_c / blocked_d，下面 4 条具体断言立刻变红。
    [Fact]
    public void 白名单外工具完全不可见_且允许的工具恰好两个()
    {
        var catalog = CreateCatalog(
            new[] { "allowed_a", "allowed_b" },
            new[] { Tool("allowed_a"), Tool("blocked_c") },
            new[] { Tool("allowed_b"), Tool("blocked_d") });

        var names = catalog.GetToolNames();

        // 该放行的：两个白名单内工具都在，条数精确为 2
        Assert.Equal(2, names.Count);
        Assert.Contains("allowed_a", names);
        Assert.Contains("allowed_b", names);
        // 该拒的：白名单外两个工具连名字都不出现，不是「看得到但调不动」
        Assert.DoesNotContain("blocked_c", names);
        Assert.DoesNotContain("blocked_d", names);
        Assert.Equal(2, catalog.GetTools().Count);
        Assert.DoesNotContain(catalog.GetTools(), t => t.Name == "blocked_c");
    }

    // [F2.1] 同名工具按名去重：三个同名实例只留一个。
    // 负控：删掉 ToolCatalog.cs:39 的 .DistinctBy(tool => tool.Name, StringComparer.Ordinal)，
    //       GetTools() 会返回 3 个，下面 Count==1 与 Single 两条断言变红。
    [Fact]
    public void 同名工具按名去重_只保留一个实例()
    {
        var catalog = CreateCatalog(
            new[] { "dup_tool" },
            new[] { Tool("dup_tool"), Tool("dup_tool") },
            new[] { Tool("dup_tool") });

        var tools = catalog.GetTools();

        Assert.Equal(1, tools.Count);
        Assert.Equal("dup_tool", tools[0].Name);
        Assert.Single(catalog.GetToolNames());
        Assert.Equal("dup_tool", Assert.Single(catalog.GetToolNames()));
    }

    // [F2.1] GetToolNames 与 GetTools 逐项一致，且保持「提供方顺序」只做过滤/去重。
    // 负控：把 ToolCatalog.cs:51 的 GetToolNames 改成返回全量（不过滤），
    //       或把顺序反转，Assert.Equal(new[]{"b","a"}, names) 变红。
    [Fact]
    public void GetToolNames_与_GetTools_逐项一致()
    {
        var catalog = CreateCatalog(
            new[] { "a", "b" },
            new[] { Tool("b"), Tool("a"), Tool("blocked") });

        var tools = catalog.GetTools();
        var names = catalog.GetToolNames();

        Assert.Equal(2, tools.Count);
        Assert.Equal(new[] { "b", "a" }, names);
        Assert.Equal(tools.Select(t => t.Name), names);
        Assert.DoesNotContain("blocked", names);
    }
}
