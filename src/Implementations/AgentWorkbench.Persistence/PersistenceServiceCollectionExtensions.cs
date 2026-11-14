using AgentWorkbench.Shared.Options;
using AgentWorkbench.Runtime.Model;
using Dapper;
using FluentMigrator.Runner;
using Microsoft.EntityFrameworkCore;
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

        // 用「工厂」而不是 Scoped DbContext：6 个 Repository 与 3 个 BackgroundService 全是 Singleton，
        // 注入 Scoped DbContext 会在启动时直接抛 "Cannot consume scoped service from singleton"。
        // 工厂模式下每个方法自建一个 DbContext，开销与原先每次 _factory.Create() 开连接相当。
        services.AddDbContextFactory<AppDbContext>(options => options
            .UseSqlite(storagePaths.ConnectionString)
            .UseSnakeCaseNamingConvention());

        services.AddSingleton<IDocumentRepository, DocumentRepository>();
        services.AddSingleton<IProviderRepository, ProviderRepository>();
        services.AddSingleton<IAgentRunRepository, AgentRunRepository>();
        services.AddSingleton<IConversationRepository, ConversationRepository>();
        services.AddSingleton<ITenantRepository, TenantRepository>();

        // 内核端口的实现：只有这一处把「数据库里的供应商」翻译成内核词汇。
        // 注册在 Persistence 侧，Runtime.Model 编译期不认识本项目。
        services.AddSingleton<IModelEndpointSource, EfModelEndpointSource>();

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
