using AgentWorkbench.Memory;
using AgentWorkbench.Memory.Persistence;
using AgentWorkbench.Memory.Persistence.Tools;
using AgentWorkbench.ToolExecution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentWorkbench.UnitTests.Memory;

/// <summary>
/// 组合根注册口径：`AddNitroMemoryPersistence` 必须把内存版存储换成落库版，
/// 且长期记忆关掉时干脆不注册记忆工具（模型看不到 = 调不动）。
/// </summary>
/// <remarks>只检查服务描述符，不真的构造服务——避免为了注册口径去搭一个数据库。</remarks>
public sealed class MemoryRegistrationTests
{
    [Fact]
    public void AddNitroMemoryPersistence_SwapsStoresAndRegistersTools()
    {
        var services = BuildServices();

        Assert.Equal(typeof(SqliteSessionStore), LastImplementation<ISessionStore>(services));
        Assert.Equal(typeof(SqliteLongTermMemoryStore), LastImplementation<ILongTermMemoryStore>(services));
        Assert.Contains(typeof(RememberTool), ToolProviders(services));
        Assert.Contains(typeof(RecallTool), ToolProviders(services));
    }

    [Fact]
    public void AddNitroMemoryPersistence_WhenLongTermDisabled_RegistersNoMemoryTools()
    {
        // 负控：关掉长期记忆后，工具必须从目录里消失。
        var services = BuildServices(new Dictionary<string, string?>
        {
            ["Harness:Memory:LongTermEnabled"] = "false"
        });

        var providers = ToolProviders(services);
        Assert.DoesNotContain(typeof(RememberTool), providers);
        Assert.DoesNotContain(typeof(RecallTool), providers);
        // 会话存储的替换与开关无关，仍然必须生效。
        Assert.Equal(typeof(SqliteSessionStore), LastImplementation<ISessionStore>(services));
    }

    private static ServiceCollection BuildServices(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var services = new ServiceCollection();
        services.AddNitroMemory(configuration);
        services.AddNitroMemoryPersistence(configuration);
        return services;
    }

    private static Type? LastImplementation<TService>(IServiceCollection services)
        => services.Last(descriptor => descriptor.ServiceType == typeof(TService)).ImplementationType;

    private static List<Type?> ToolProviders(IServiceCollection services)
        => services
            .Where(descriptor => descriptor.ServiceType == typeof(IAgentToolProvider))
            .Select(descriptor => descriptor.ImplementationType)
            .ToList();
}
