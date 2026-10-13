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

    /// <summary>
    /// L1 热重载的行为契约：白名单住进 appsettings.json 后，改配置下个 run 生效，无需停服。
    /// 负控意义：若 ToolPolicy 退回持 IOptions（静态快照），本用例第二组断言必须失败。
    /// </summary>
    [Fact]
    public void IsAllowed_ReflectsConfigChangeWithoutRestart()
    {
        var monitor = new MutableOptionsMonitor(new ToolPolicyOptions { Whitelist = ["a"] });
        var policy = new ToolPolicy(monitor);

        Assert.True(policy.IsAllowed("a"));
        Assert.False(policy.IsAllowed("b"));

        // 模拟 appsettings.json 热重载：白名单换人，服务不重启
        monitor.Update(new ToolPolicyOptions { Whitelist = ["b"] });

        Assert.False(policy.IsAllowed("a"));
        Assert.True(policy.IsAllowed("b"));
    }

    private static ToolCatalog BuildCatalog(IServiceProvider provider, ToolPolicyOptions policy)
        => new(provider, new ToolPolicy(new MutableOptionsMonitor(policy)), NullLogger<ToolCatalog>.Instance);

    private sealed class FakeToolProvider(string name) : IAgentToolProvider
    {
        public IReadOnlyList<AITool> GetTools() => [AIFunctionFactory.Create(() => name, name)];
    }

    /// <summary>可变的 IOptionsMonitor 测试替身：Update 模拟配置源热重载后的新值。</summary>
    private sealed class MutableOptionsMonitor(ToolPolicyOptions initial) : IOptionsMonitor<ToolPolicyOptions>
    {
        private ToolPolicyOptions _current = initial;

        public ToolPolicyOptions CurrentValue => _current;

        public ToolPolicyOptions Get(string? name) => _current;

        public IDisposable OnChange(Action<ToolPolicyOptions, string?> listener) => new NoopDisposable();

        public void Update(ToolPolicyOptions value) => _current = value;

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
