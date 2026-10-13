using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentWorkbench.Safety;

/// <summary>Safety 模块（可靠性与安全）：工具授权策略与工具审批策略的注册入口。</summary>
public static class SafetyServiceCollectionExtensions
{
    public static IServiceCollection AddNitroSafety(
        this IServiceCollection services, IConfiguration configuration)
    {
        // 第一维：能力白名单（能不能用）。第二维：审批级别（要不要先问人）。
        // 两维各自一条 IOptionsMonitor 链路，改配置即热重载，互不影响。
        services.AddOptions<ToolPolicyOptions>()
            .Bind(configuration.GetSection(ToolPolicyOptions.SectionName));
        services.AddOptions<ToolApprovalOptions>()
            .Bind(configuration.GetSection(ToolApprovalOptions.SectionName));

        services.AddSingleton<IToolPolicy, ToolPolicy>();
        services.AddSingleton<IToolApprovalPolicy, ToolApprovalPolicy>();
        return services;
    }
}
