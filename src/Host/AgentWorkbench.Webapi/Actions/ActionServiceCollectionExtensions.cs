namespace AgentWorkbench.Webapi.Actions;

using AgentWorkbench.ActionGateway;
using AgentWorkbench.Erp;
using AgentWorkbench.Erp.ErpNext;
using AgentWorkbench.Gitea;
using AgentWorkbench.LabBooking;
using AgentWorkbench.Persistence;
using AgentWorkbench.Persistence.ActionStore;
using AgentWorkbench.Ports;
using AgentWorkbench.Tools.Abstractions;
using AgentWorkbench.Tools.Builtin;
using AgentWorkbench.Tools.Mcp;

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

    /// <summary>ERP 执行体的传输方式：Http（默认）或 Mcp。换手不换闸。</summary>
    public const string ErpTransportConfigKey = "Actions:ErpTransport";

    /// <summary>查 ERP 单据用的工具全名；只在 Mcp 传输下有意义。</summary>
    public const string ErpMcpToolConfigKey = "Actions:ErpNext:McpTool";

    /// <summary>提交 ERP 单据用的工具全名；只在 Mcp 传输下有意义。</summary>
    public const string ErpMcpSubmitToolConfigKey = "Actions:ErpNext:McpSubmitTool";

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

        services.AddSingleton<IActionSet>(sp => ResolveActionSet(configuration, sp));
        services.AddSingleton<ActionRunService>();

        // 直连工具的动作闸：模型自己发起的 MCP 工具调用从这里进四道闸。
        // 只翻译身份，判定全部在 ActionRunService —— 全仓只有一份「谁批准的」答案。
        services.AddSingleton<IToolCallGateway>(sp => new ActionToolGateway(
            sp.GetRequiredService<ActionRunService>(),
            sp.GetRequiredService<IActionSubjectProvider>(),
            ResolveToolPolicies(configuration)));

        // 方案第 4 步：一个「填单工具」。能力清单来自注入的 IActionSet.Specs，
        // 工具本身业务无关，换域时自动换成新域的能力描述。
        services.AddSingleton<IAgentToolProvider, ProposeActionTool>();
        return services;
    }

    /// <summary>默认落库；只有显式写 InMemory 才退回内存实现。</summary>
    private static bool UseSqliteStore(IConfiguration configuration)
        => !string.Equals(configuration[StoreConfigKey], "InMemory", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 直连工具的策略表，按当前动作域取。没有策略的域返回空表 ——
    /// 空表意味着「所有直连工具都认不出来」，也就是一律拒，而不是一律放行。
    /// </summary>
    private static IReadOnlyList<ToolCallPolicy> ResolveToolPolicies(IConfiguration configuration)
    {
        var name = configuration[ActionSetConfigKey];

        if (string.Equals(name, FakeActionSetName, StringComparison.OrdinalIgnoreCase))
            return [];

        if (string.Equals(name, GiteaActionSetName, StringComparison.OrdinalIgnoreCase))
            return [];

        return ErpToolPolicies.All();
    }

    /// <summary>
    /// 换域只有这一处分支。三个域都实现同一个 <see cref="IActionSet"/>，
    /// 而翻译工具只按接口读清单 —— 所以这里换成谁，模型看到的能力清单就变成谁的，<c>src/Core</c> 不动。
    /// 未知名字退回 Erp（默认域）而不抛异常：配置写错时宁可跑默认域，也不要宿主起不来。
    /// </summary>
    private static IActionSet ResolveActionSet(IConfiguration configuration, IServiceProvider services)
    {
        var name = configuration[ActionSetConfigKey];

        if (string.Equals(name, FakeActionSetName, StringComparison.OrdinalIgnoreCase))
            return new FakeActionSet();

        if (string.Equals(name, GiteaActionSetName, StringComparison.OrdinalIgnoreCase))
            return new GiteaActionSet(ReadGiteaOptions(configuration));

        return ErpActionSetWithRealTransport(configuration, services);
    }

    /// <summary>
    /// ERP 域的执行路径选择。判据只有一处：<c>Actions:ErpNext:BaseUrl</c> 是否有值。
    /// <list type="bullet">
    /// <item>没配 → 内存假件（本地演练）；</item>
    /// <item>配了 → 真执行体，<b>传输方式</b>由 <c>Actions:ErpTransport</c> 决定：</item>
    /// </list>
    /// <c>Http</c>（默认）走本仓的 <see cref="HttpErpTransport"/>；<c>Mcp</c> 走闸后面的
    /// <see cref="McpActionExecutor"/> → <c>IToolInvoker</c> → 外部 MCP server。
    /// 两者共用同一套 ActionSpec、同一套四道闸、同一套审计 —— 换的是手，不是判断。
    /// <para>
    /// 配了一半（缺 key / secret / 时区）**仍然**走真执行体，由适配器以 <c>configuration_missing</c>
    /// 明确拒绝 —— 不回落假件（M2 设计 §3.1 决定 3：最糟的不是「不能写」，而是
    /// 「以为写了，其实写进了内存假 ERP」）。
    /// </para>
    /// </summary>
    private static IActionSet ErpActionSetWithRealTransport(IConfiguration configuration, IServiceProvider services)
    {
        var options = ReadErpNextOptions(configuration);
        if (options is null)
            return new ErpActionSet();

        if (UseMcpTransport(configuration))
        {
            var invoker = services.GetRequiredService<IToolInvoker>();
            var bindings = ErpMcpBindings.All(
                configuration[ErpMcpToolConfigKey],
                configuration[ErpMcpSubmitToolConfigKey]);
            return new ErpActionSet(new McpActionExecutor(invoker, bindings));
        }

        // 超时由 ErpNextOptions.EffectiveTimeout 逐次控制，HttpClient 自己不要再截一刀，
        // 否则「超时归 Unknown」与「调用方取消」会混在一起。
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpErpTransport(http, options);
        return new ErpActionSet(new ErpNextQueryAdapter(transport, options));
    }

    /// <summary>ERP 执行体的传输方式：<c>Http</c>（默认）或 <c>Mcp</c>。写错名字按 Http 处理。</summary>
    private static bool UseMcpTransport(IConfiguration configuration)
        => string.Equals(configuration[ErpTransportConfigKey], "Mcp", StringComparison.OrdinalIgnoreCase);

    /// <summary>ERPNext 连接参数。只读 BaseUrl 判断「要不要真连」，四项齐不齐交给 IsConfigured 判。</summary>
    private static ErpNextOptions? ReadErpNextOptions(IConfiguration configuration)
    {
        var baseUrl = configuration["Actions:ErpNext:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var doctype = configuration["Actions:ErpNext:PurchaseOrderDoctype"];
        return new ErpNextOptions(
            BaseUrl: baseUrl.TrimEnd('/'),
            ApiKey: configuration["Actions:ErpNext:ApiKey"],
            ApiSecret: configuration["Actions:ErpNext:ApiSecret"],
            TimeZoneId: configuration["Actions:ErpNext:TimeZoneId"],
            PurchaseOrderDoctype: string.IsNullOrWhiteSpace(doctype) ? "Purchase Order" : doctype,
            Timeout: ReadSeconds(configuration["Actions:ErpNext:TimeoutSeconds"]),
            MaxReadRetries: int.TryParse(configuration["Actions:ErpNext:MaxReadRetries"], out var retries) && retries >= 0
                ? retries
                : 2);
    }

    private static TimeSpan? ReadSeconds(string? raw)
        => int.TryParse(raw, out var seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

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
