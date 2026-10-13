using AgentWorkbench.ToolExecution.Builtin;
using Microsoft.Extensions.Configuration;

namespace AgentWorkbench.ToolExecution;

/// <summary>
/// ToolExecution 模块（工具调用）：工具注册扩展点 + 白名单目录 + 函数调用环执行 + 通用内置工具
/// + 工具授权/审批两轴策略。
/// 与业务/数据域解耦：知识库工具在 Knowledge.Local，文档工具在 Documents。
/// </summary>
public static class ToolExecutionServiceCollectionExtensions
{
    public static IServiceCollection AddNitroToolExecution(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ToolExecutionOptions>()
            .Bind(configuration.GetSection(ToolExecutionOptions.SectionName));

        // 安全两轴（原独立 Safety 项目，阶段 4 并入本模块——合并的是项目边界，能力一字未动）：
        // 第一轴「能不能用」（能力白名单，目录层），第二轴「要不要先问人」（审批级别，执行层）。
        // 两轴各自一条 IOptionsMonitor 链路，改配置即热重载，互不影响。
        services.AddOptions<ToolPolicyOptions>()
            .Bind(configuration.GetSection(ToolPolicyOptions.SectionName));
        services.AddOptions<ToolApprovalOptions>()
            .Bind(configuration.GetSection(ToolApprovalOptions.SectionName));
        services.AddSingleton<IToolPolicy, ToolPolicy>();
        services.AddSingleton<IToolApprovalPolicy, ToolApprovalPolicy>();

        services.AddSingleton<IToolExecutionPipeline, ToolExecutionPipeline>();
        services.AddSingleton<IToolCatalog, ToolCatalog>();

        services.AddSingleton<IAgentToolProvider, GetCurrentTimeTool>();
        services.AddSingleton<IAgentToolProvider, CalculatorTool>();
        return services;
    }
}
