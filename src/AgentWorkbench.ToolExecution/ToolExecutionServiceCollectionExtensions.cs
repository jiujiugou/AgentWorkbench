using AgentWorkbench.ToolExecution.Builtin;
using Microsoft.Extensions.Configuration;

namespace AgentWorkbench.ToolExecution;

/// <summary>
/// ToolExecution 模块（工具调用）：工具注册扩展点 + 白名单目录 + 函数调用环执行 + 通用内置工具。
/// 与业务/数据域解耦：知识库工具在 Knowledge.Local，文档工具在 Documents。
/// </summary>
public static class ToolExecutionServiceCollectionExtensions
{
    public static IServiceCollection AddNitroToolExecution(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ToolExecutionOptions>()
            .Bind(configuration.GetSection(ToolExecutionOptions.SectionName));

        services.AddSingleton<IToolExecutionPipeline, ToolExecutionPipeline>();
        services.AddSingleton<IToolCatalog, ToolCatalog>();

        services.AddSingleton<IAgentToolProvider, GetCurrentTimeTool>();
        services.AddSingleton<IAgentToolProvider, CalculatorTool>();
        return services;
    }
}
