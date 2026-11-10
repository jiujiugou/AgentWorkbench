using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgentWorkbench.Tools.Abstractions;

namespace AgentWorkbench.Tools.Mcp;

/// <summary>把 MCP 客户端接进工具层。</summary>
public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// 注册 MCP 工具来源。工具在启动期一次性拉取，不参与热重载——
    /// 改了 <c>Mcp:Servers</c> 要重启进程；这条限制是刻意的（见 README）。
    /// </summary>
    public static IServiceCollection AddNitroMcp(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<McpOptions>(configuration.GetSection(McpOptions.SectionName));
        services.AddSingleton<McpToolRegistry>();
        services.AddHostedService<McpConnectionHostedService>();
        services.AddSingleton<IAgentToolProvider, McpToolProvider>();
        return services;
    }
}

/// <summary>启动期建立 MCP 连接。失败只记日志，不阻止进程启动。</summary>
public sealed class McpConnectionHostedService(McpToolRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => registry.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
