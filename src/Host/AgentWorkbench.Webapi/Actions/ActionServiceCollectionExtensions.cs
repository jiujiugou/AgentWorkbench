namespace AgentWorkbench.Webapi.Actions;

using AgentWorkbench.ActionGateway;
using AgentWorkbench.Erp;
using AgentWorkbench.LabBooking;
using AgentWorkbench.Ports;

/// <summary>
/// M1 动作链装配（组合根）。这是全仓唯一同时认识底座端口与动作域的地方（M1 设计 §7）。
/// </summary>
public static class ActionServiceCollectionExtensions
{
    /// <summary>换域开关。AC-01 的验收动作就是改这一个配置，src/Core 一行不动。</summary>
    public const string ActionSetConfigKey = "Actions:ActionSet";

    public static IServiceCollection AddNitroActions(this IServiceCollection services, IConfiguration configuration)
    {
        // 内存实现：M1 用假实现，重启即丢；M2 接真 ERP 时替换 IActionExecutor 与存储。
        services.AddSingleton<InMemoryActionRunStore>();
        services.AddSingleton<InMemoryTraceStore>();
        services.AddSingleton<IActionRunStore>(sp => sp.GetRequiredService<InMemoryActionRunStore>());
        services.AddSingleton<ITraceSink>(sp => sp.GetRequiredService<InMemoryTraceStore>());
        services.AddSingleton<IAuditQuery>(sp => sp.GetRequiredService<InMemoryTraceStore>());

        services.AddSingleton<IActionSet>(_ => ResolveActionSet(configuration[ActionSetConfigKey]));
        services.AddSingleton<ActionRunService>();
        return services;
    }

    private static IActionSet ResolveActionSet(string? configuredName)
        => string.Equals(configuredName, "FakeActionSet", StringComparison.OrdinalIgnoreCase)
            ? new FakeActionSet()
            : new ErpActionSet();
}
