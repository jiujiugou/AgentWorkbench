namespace AgentWorkbench.Webapi.Actions;

using AgentWorkbench.ActionGateway;
using AgentWorkbench.Erp;
using AgentWorkbench.Gitea;
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

    /// <summary>
    /// 三个域名的唯一来源。写成常量而不是散落的字符串，
    /// 是为了让「换域只改配置」这句话可被测试直接引用。
    /// </summary>
    public const string ErpActionSetName = "ErpActionSet";
    public const string FakeActionSetName = "FakeActionSet";
    public const string GiteaActionSetName = "GiteaActionSet";

    public static IServiceCollection AddNitroActions(this IServiceCollection services, IConfiguration configuration)
    {
        // 身份委托：HTTP 入口、Hub 链路与翻译工具共用同一个只读端口。
        // 端口不含写入语义，宿主负责把当前认证主体放进调用作用域。
        services.AddSingleton<ActionSubjectProvider>();
        services.AddSingleton<IActionSubjectProvider>(sp => sp.GetRequiredService<ActionSubjectProvider>());
        services.AddSingleton<IActionOriginProvider>(sp => sp.GetRequiredService<ActionSubjectProvider>());
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
            // 追溯页的列表入口：只读，与 IAuditQuery 同源（都读 action_trace），不启动任何东西。
            services.AddSingleton<IActionHistoryQuery>(sp => sp.GetRequiredService<SqliteTraceStore>());
        }
        else
        {
            services.AddSingleton<InMemoryActionRunStore>();
            services.AddSingleton<InMemoryTraceStore>();
            services.AddSingleton<IActionRunStore>(sp => sp.GetRequiredService<InMemoryActionRunStore>());
            services.AddSingleton<ITraceSink>(sp => sp.GetRequiredService<InMemoryTraceStore>());
            services.AddSingleton<IAuditQuery>(sp => sp.GetRequiredService<InMemoryTraceStore>());
            services.AddSingleton<IActionHistoryQuery>(sp => sp.GetRequiredService<InMemoryTraceStore>());
        }

        services.AddSingleton<IActionSet>(_ => ResolveActionSet(configuration));
        services.AddSingleton<ActionRunService>();

        // 方案第 4 步：一个「填单工具」。能力清单来自注入的 IActionSet.Specs，
        // 工具本身业务无关，换域时自动换成新域的能力描述。
        services.AddSingleton<IAgentToolProvider, ProposeActionTool>();
        return services;
    }

    /// <summary>默认落库；只有显式写 InMemory 才退回内存实现。</summary>
    private static bool UseSqliteStore(IConfiguration configuration)
        => !string.Equals(configuration[StoreConfigKey], "InMemory", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 换域只有这一处分支。三个域都实现同一个 <see cref="IActionSet"/>，
    /// 而翻译工具只按接口读清单 —— 所以这里换成谁，模型看到的能力清单就变成谁的，<c>src/Core</c> 不动。
    /// 未知名字退回 Erp（默认域）而不抛异常：配置写错时宁可跑默认域，也不要宿主起不来。
    /// </summary>
    private static IActionSet ResolveActionSet(IConfiguration configuration)
    {
        var name = configuration[ActionSetConfigKey];

        if (string.Equals(name, FakeActionSetName, StringComparison.OrdinalIgnoreCase))
            return new FakeActionSet();

        if (string.Equals(name, GiteaActionSetName, StringComparison.OrdinalIgnoreCase))
            return new GiteaActionSet(ReadGiteaOptions(configuration));

        return new ErpActionSet();
    }

    /// <summary>
    /// Gitea 连接参数。缺 BaseUrl / Token 时返回 <see cref="GiteaOptions.Unconfigured"/>：
    /// 动作一律 fail-closed 且<b>零 HTTP</b>，但 <c>Specs.ListAsync()</c> 照常返回清单 ——
    /// 所以「让模型看到业务能力清单」这件事不需要真 token 也能验证。
    /// </summary>
    private static GiteaOptions ReadGiteaOptions(IConfiguration configuration)
    {
        var baseUrl = configuration["Actions:Gitea:BaseUrl"];
        var token = configuration["Actions:Gitea:Token"];
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
            return GiteaOptions.Unconfigured;

        return new GiteaOptions(
            BaseUrl: baseUrl,
            Token: token,
            DefaultOwner: configuration["Actions:Gitea:DefaultOwner"] ?? "",
            DefaultRepo: configuration["Actions:Gitea:DefaultRepo"] ?? "",
            TimeoutSeconds: ReadInt(configuration["Actions:Gitea:TimeoutSeconds"], 15),
            // 写能力默认关闭（M2 只读边界）：显式配 true 才开，且开了仍要人工审批。
            AllowWrites: ReadBool(configuration["Actions:Gitea:AllowWrites"]));
    }

    private static int ReadInt(string? raw, int fallback)
        => int.TryParse(raw, out var value) && value > 0 ? value : fallback;

    private static bool ReadBool(string? raw)
        => bool.TryParse(raw, out var value) && value;
}
