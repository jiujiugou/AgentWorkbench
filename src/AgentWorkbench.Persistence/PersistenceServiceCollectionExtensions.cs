using AgentWorkbench.Shared.Options;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Persistence;

/// <summary>Persistence 模块：连接、迁移、仓储的 DI 注册。</summary>
public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddNitroPersistence(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName));

        var storageOptions = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()
            ?? new StorageOptions();
        var storagePaths = new StoragePaths(environment, Options.Create(storageOptions));
        services.AddSingleton(storagePaths);

        services.AddSingleton<IDbConnectionFactory, SqliteDbConnectionFactory>();
        services.AddSingleton<IDocumentRepository, DocumentRepository>();
        services.AddSingleton<IProviderRepository, ProviderRepository>();
        services.AddSingleton<IRuleRepository, RuleRepository>();
        services.AddSingleton<IReviewRepository, ReviewRepository>();
        services.AddSingleton<IAdvisorRepository, AdvisorRepository>();
        services.AddSingleton<IAgentRunRepository, AgentRunRepository>();
        services.AddSingleton<IConversationRepository, ConversationRepository>();
        services.AddSingleton<IWorkflowRepository, WorkflowRepository>();

        services.AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddSQLite()
                .WithGlobalConnectionString(storagePaths.ConnectionString)
                .ScanIn(typeof(PersistenceServiceCollectionExtensions).Assembly).For.Migrations());

        return services;
    }

    /// <summary>启动时执行迁移（在 app 构建之后、处理请求之前调用）。</summary>
    public static void InitializeDatabase(this IHost app)
    {
        using var scope = app.Services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();
    }
}
