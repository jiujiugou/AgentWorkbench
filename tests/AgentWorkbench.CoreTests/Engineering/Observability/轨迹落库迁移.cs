using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentWorkbench.Observability;
using AgentWorkbench.Persistence;
using AgentWorkbench.Shared;
using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentWorkbench.CoreTests.Engineering.Observability;

/// <summary>
/// 轨迹落库的表结构 (M033) 与记录形状的对照。
///
/// 为什么这属内核的事：内核产出的事实如果落不下来，回放读回来是空——「字段有了、读回来没有」
/// 比没有这个字段更糟，因为它看起来是完整的。这条测试钉的正是那个断点。
///
/// 边界：这里验的是「同一条 record 写得进去、读得回来、值不变」，
/// 不验检索面、保留期、报表——那些是应用层（部件文档第六节已划出）。
/// </summary>
public sealed class 轨迹落库迁移 : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _provider;

    public 轨迹落库迁移()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<AppDbContext>(options => options
            .UseSqlite($"Data Source={_path};Pooling=False")
            .UseSnakeCaseNamingConvention());
        services.AddFluentMigratorCore().ConfigureRunner(runner => runner
            .AddSQLite()
            .WithGlobalConnectionString($"Data Source={_path};Pooling=False")
            .ScanIn(typeof(PersistenceServiceCollectionExtensions).Assembly).For.Migrations());

        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    /// <summary>记录形状上的事实 → 表里的列。加字段时必须同时改这里与迁移，否则读回来是空。</summary>
    private static readonly (string Table, string Column)[] ExpectedColumns =
    [
        ("agent_runs", "input_tokens"),
        ("agent_runs", "output_tokens"),
        ("agent_runs", "complete"),
        ("agent_runs", "stop_kind"),
        ("agent_runs", "stop_observed"),
        ("agent_runs", "stop_limit"),
        ("agent_runs", "stop_subject"),
        ("agent_steps", "truncated"),
    ];

    // 负控：从迁移里删掉任何一列（或表名 / 列名拼错一个字母）→ 本条红，并指出缺哪一列。
    [Fact]
    public void 记录形状上的事实在表里都有列()
    {
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        connection.Open();

        var missing = ExpectedColumns
            .Where(c =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{c.Table}') WHERE name = '{c.Column}'";
                return (long)command.ExecuteScalar()! == 0;
            })
            .Select(c => $"{c.Table}.{c.Column}")
            .ToList();

        Assert.Empty(missing);
    }

    // 负控：Mapping 少写任一字段（例如 Map 里不读 run.Complete）→ 读回来与写进去不等，本条红。
    // 这是「内核产出 → 应用层接收」唯一一条闭环证据；没有它，前面所有断言都只到「交出去了」。
    [Fact]
    public async Task 写进去的事实能原样读回来()
    {
        var factory = _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var store = new ExecutionTraceStore(
            new AgentRunRepository(factory, NullLogger<AgentRunRepository>.Instance),
            NullLogger<ExecutionTraceStore>.Instance);

        var trace = ExecutionTrace.Create(
            "s-roundtrip", AgentRunStatus.LoopDetected, AgentRunCode.LoopDetected, 2, "test-model", 120,
            "模型连续 3 次以相同参数重复调用工具 echo",
            [new ExecutionStep(0, "tool", "echo", "{\"text\":\"hi\"}", "echo:hi", null, Truncated: true)],
            inputTokens: 10, outputTokens: 5, complete: false,
            stop: new RunStopDetail("repeated_tool_calls", 3, 3, "echo"));

        var runId = await store.SaveAsync(trace);
        var back = await store.GetAsync(runId);

        Assert.NotNull(back);
        Assert.Equal(10L, back!.InputTokens);
        Assert.Equal(5L, back.OutputTokens);
        Assert.False(back.Complete);
        Assert.NotNull(back.Stop);
        Assert.Equal("repeated_tool_calls", back.Stop!.Kind);
        Assert.Equal(3L, back.Stop.Observed);
        Assert.Equal(3L, back.Stop.Limit);
        Assert.Equal("echo", back.Stop.Subject);
        Assert.True(Assert.Single(back.Steps).Truncated);
    }

    public void Dispose()
    {
        // SQLite 连接池会抓住文件句柄；不清池就删不掉临时库。
        SqliteConnection.ClearAllPools();
        _provider.Dispose();
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
