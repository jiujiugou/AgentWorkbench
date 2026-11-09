namespace AgentWorkbench.Webapi.Actions;

using AgentWorkbench.ActionGateway;
using AgentWorkbench.Erp;
using AgentWorkbench.LabBooking;
using AgentWorkbench.Persistence;
using AgentWorkbench.Persistence.ActionStore;
using AgentWorkbench.Ports;
using AgentWorkbench.Tools.Abstractions;
using AgentWorkbench.Tools.Builtin;

/// <summary>
/// M1 动作链装配（组合根）。这是全仓唯一同时认识底座端口与动作域的地方（M1 设计 §7）。
///
/// 本文件只做「把哪个实例插进哪个槽」的决定，不含任何机制或业务判断：
/// 换动作域改 <c>Actions:ActionSet</c>，换存储介质改 <c>Actions:Store</c>，
/// <c>src/Core</c> 一行不动。
/// </summary>
public static class ActionServiceCollectionExtensions
{
    /// <summary>换域开关。AC-01 的验收动作就是改这一个配置，src/Core 一行不动。</summary>
    public const string ActionSetConfigKey = "Actions:ActionSet";

    /// <summary>存储开关：Sqlite（默认，重启不丢）/ InMemory（仅本地演练与诊断）。</summary>
    public const string StoreConfigKey = "Actions:Store";

    public static IServiceCollection AddNitroActions(this IServiceCollection services, IConfiguration configuration)
    {
        // 身份委托：HTTP 入口、Hub 链路与翻译工具共用同一个只读端口。
        // 端口不含写入语义，宿主负责把当前认证主体放进调用作用域。
        services.AddSingleton<ActionSubjectProvider>();
        services.AddSingleton<IActionSubjectProvider>(sp => sp.GetRequiredService<ActionSubjectProvider>());
        services.AddSingleton<IActionSubjectScope>(sp => sp.GetRequiredService<ActionSubjectProvider>());

        if (UseSqliteStore(configuration))
        {
            // 只换存储介质：四道网关、ActionRunService 的顺序与失败默认一概不动。
            // 幂等领取由 action_idempotency 的主键唯一约束保证，不再依赖进程内字典。
            services.AddSingleton<SqliteActionRunStore>();
            services.AddSingleton<SqliteTraceStore>();
            services.AddSingleton<IActionRunStore>(sp => sp.GetRequiredService<SqliteActionRunStore>());
            services.AddSingleton<ITraceSink>(sp => sp.GetRequiredService<SqliteTraceStore>());
            services.AddSingleton<IAuditQuery>(sp => sp.GetRequiredService<SqliteTraceStore>());
        }
        else
        {
            services.AddSingleton<InMemoryActionRunStore>();
            services.AddSingleton<InMemoryTraceStore>();
            services.AddSingleton<IActionRunStore>(sp => sp.GetRequiredService<InMemoryActionRunStore>());
            services.AddSingleton<ITraceSink>(sp => sp.GetRequiredService<InMemoryTraceStore>());
            services.AddSingleton<IAuditQuery>(sp => sp.GetRequiredService<InMemoryTraceStore>());
        }

        services.AddSingleton<IActionSet>(_ => ResolveActionSet(configuration[ActionSetConfigKey]));
        services.AddSingleton<ActionRunService>();

        // 方案第 4 步：一个「填单工具」。能力清单来自注入的 IActionSet.Specs，
        // 工具本身业务无关，换域时自动换成新域的能力描述。
        services.AddSingleton<IAgentToolProvider, ProposeActionTool>();
        return services;
    }

    /// <summary>默认落库；只有显式写 InMemory 才退回内存实现。</summary>
    private static bool UseSqliteStore(IConfiguration configuration)
        => !string.Equals(configuration[StoreConfigKey], "InMemory", StringComparison.OrdinalIgnoreCase);

    private static IActionSet ResolveActionSet(string? configuredName)
        => string.Equals(configuredName, "FakeActionSet", StringComparison.OrdinalIgnoreCase)
            ? new FakeActionSet()
            : new ErpActionSet();
}
