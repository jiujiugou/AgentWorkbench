using AgentWorkbench.Persistence;
using FluentMigrator.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace AgentWorkbench.UnitTests.Memory;

/// <summary>
/// 一次性临时 SQLite 库：跑真实 FluentMigrator 迁移，用完连目录一起删。
/// 用它才能在单测里验证「重启后同一 sessionId 还能续上」，而不必真的重启进程。
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _dir;
    private readonly ServiceProvider _services;

    public IDbConnectionFactory ConnectionFactory { get; }

    public TempDatabase()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aw-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(_dir);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataDir"] = "data",
                ["Storage:UploadsDir"] = "data/uploads",
                ["Storage:DbPath"] = "data/test.db"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNitroPersistence(configuration, environment.Object);
        _services = services.BuildServiceProvider();

        using (var scope = _services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();

        ConnectionFactory = _services.GetRequiredService<IDbConnectionFactory>();
    }

    public void Dispose()
    {
        _services.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录，删不掉不影响测试结论。
        }
    }
}
