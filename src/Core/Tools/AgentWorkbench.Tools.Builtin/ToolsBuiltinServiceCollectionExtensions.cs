using AgentWorkbench.Tools.Abstractions;

namespace AgentWorkbench.Tools.Builtin;

/// <summary>
/// 内置通用工具注册：calculator / get_current_time。
/// 与业务无关的最小工具集；业务工具（文档 / 知识 / 记忆）由各自模块自行注册 <see cref="IAgentToolProvider"/>。
/// </summary>
public static class ToolsBuiltinServiceCollectionExtensions
{
    public static IServiceCollection AddNitroBuiltinTools(this IServiceCollection services)
    {
        services.AddSingleton<IAgentToolProvider, GetCurrentTimeTool>();
        services.AddSingleton<IAgentToolProvider, CalculatorTool>();
        return services;
    }
}
