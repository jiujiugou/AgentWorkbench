using AgentWorkbench.Domain;
using AgentWorkbench.Persistence;
using FluentMigrator.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentWorkbench.DataIntegrationTests;

/// <summary>
/// 五个非 Workflow 仓储换 EF 后的行为等价性。
/// </summary>
/// <remarks>
/// 表结构由真实迁移建立（不是手写 DDL）：换实现时最容易漂的就是「列名/可空性/默认值」，
/// 用迁移建表才能保证测的是线上那套 schema。
/// 重点盯三处改写过语义的地方：
/// <list type="number">
/// <item><c>FindUserAsync</c> 的大小写不敏感（原 SQL 显式 <c>COLLATE NOCASE</c>）；</item>
/// <item><c>UpdateProviderAsync</c> 的 <c>COALESCE(@apiKey, api_key)</c>（传 null 表示不改）；</item>
/// <item>自增主键回填（原来靠 <c>SELECT last_insert_rowid()</c>）。</item>
/// </list>
/// </remarks>
public sealed class RepositoryParityTests : IDisposable
{
    private readonly string _root;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public RepositoryParityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "aw-parity-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
        var dbPath = Path.Combine(_root, "parity.db");

        var paths = new StoragePaths(
            new FakeEnv(_root),
            Microsoft.Extensions.Options.Options.Create(new AgentWorkbench.Shared.Options.StorageOptions
            {
                DataDir = _root,
                UploadsDir = Path.Combine(_root, "uploads"),
                DbPath = dbPath
            }));

        MigrateUp(paths.ConnectionString);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(paths.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        _factory = new Factory(options);
    }

    // ── Identity ────────────────────────────────────────────

    [Fact]
    public async Task 用户名查找不区分大小写()
    {
        var repo = new IdentityRepository(_factory);

        await repo.CreateTenantAsync("测试租户");
        var tenant = (await repo.ListTenantsAsync()).Single();
        await repo.CreateUserAsync(tenant.Id, "WangZiHeng", "王紫恒", "hash");

        // 原 SQL 是 user_name = @userName COLLATE NOCASE：登录框输入大小写不同不该找不到账号。
        Assert.NotNull(await repo.FindUserAsync("wangziheng"));
        Assert.NotNull(await repo.FindUserAsync("WANGZIHENG"));
        Assert.Null(await repo.FindUserAsync("wangziheng2"));
        Assert.Equal(1, await repo.CountUsersAsync());
    }

    // ── Conversations ───────────────────────────────────────

    [Fact]
    public async Task 会话按租户与用户隔离()
    {
        var repo = new ConversationRepository(_factory, NullLogger<ConversationRepository>.Instance);

        var mine = await repo.CreateAsync("t1", "u1", "我的会话");

        Assert.NotNull(await repo.GetAsync(mine.Id, "t1", "u1"));
        Assert.Null(await repo.GetAsync(mine.Id, "t2", "u1")); // 换租户看不到
        Assert.Null(await repo.GetAsync(mine.Id, "t1", "u2")); // 换用户看不到

        // GetOrCreate 用别人的 id 时不复用、不报错，直接新建
        var other = await repo.GetOrCreateAsync(mine.Id, "t2", "u1");
        Assert.NotEqual(mine.Id, other.Id);

        Assert.Single(await repo.ListAsync("t1", "u1"));
        Assert.False(await repo.DeleteAsync(mine.Id, "t2", "u1")); // 越权删不掉
        Assert.True(await repo.DeleteAsync(mine.Id, "t1", "u1"));
    }

    [Fact]
    public async Task 追加消息同时推进会话更新时间()
    {
        var repo = new ConversationRepository(_factory, NullLogger<ConversationRepository>.Instance);
        var conversation = await repo.CreateAsync("t1", "u1");

        var before = (await repo.GetAsync(conversation.Id, "t1", "u1"))!.UpdatedAt;

        await repo.AddMessageAsync(conversation.Id, "user", "你好");
        await repo.AddMessageAsync(conversation.Id, "assistant", "在");

        var messages = await repo.GetMessagesAsync(conversation.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal(["user", "assistant"], messages.Select(m => m.Role));
        Assert.Equal(2, await repo.CountMessagesAsync(conversation.Id));

        var after = (await repo.GetAsync(conversation.Id, "t1", "u1"))!.UpdatedAt;
        Assert.True(string.CompareOrdinal(after, before) >= 0, "会话 updated_at 应随消息推进");
    }

    // ── Providers ───────────────────────────────────────────

    [Fact]
    public async Task 首个供应商自动激活且切换互斥()
    {
        var repo = new ProviderRepository(_factory, NullLogger<ProviderRepository>.Instance);

        var first = await repo.CreateProviderAsync("siliconflow", "https://api.siliconflow.cn/v1", "k1");
        Assert.True(first.Id > 0, "自增 id 必须回填"); // 原来靠 SELECT last_insert_rowid()
        Assert.Equal(1, (await repo.GetActiveProviderAsync())!.Id);

        var second = await repo.CreateProviderAsync("zhipu", "https://open.bigmodel.cn/api/paas/v4", "k2");
        Assert.Equal(first.Id, (await repo.GetActiveProviderAsync())!.Id); // 已有激活项时不抢

        Assert.True(await repo.ActivateProviderAsync(second.Id));
        var active = await repo.GetActiveProviderAsync();
        Assert.Equal(second.Id, active!.Id);

        var all = await repo.ListProvidersAsync();
        Assert.Single(all, p => p.IsActive == 1); // 激活是互斥的
        Assert.Equal(second.Id, all[0].Id);             // 激活项排第一
    }

    [Fact]
    public async Task 更新供应商时传空key表示不改()
    {
        var repo = new ProviderRepository(_factory, NullLogger<ProviderRepository>.Instance);
        var provider = await repo.CreateProviderAsync("p", "https://x/v1", "orig-key");

        // 原 SQL：api_key = COALESCE(@apiKey, api_key)
        Assert.True(await repo.UpdateProviderAsync(provider.Id, "p2", "https://y/v1", apiKey: null));
        var afterNull = await repo.GetProviderAsync(provider.Id);
        Assert.Equal("orig-key", afterNull!.ApiKey);
        Assert.Equal("p2", afterNull.Name);

        Assert.True(await repo.UpdateProviderAsync(provider.Id, "p3", "https://y/v1", apiKey: "new-key"));
        Assert.Equal("new-key", (await repo.GetProviderAsync(provider.Id))!.ApiKey);

        Assert.False(await repo.UpdateProviderAsync(999999, "x", "y", null)); // 不存在返回 false
    }

    [Fact]
    public async Task 首个模型成为默认删掉默认后顶上()
    {
        var repo = new ProviderRepository(_factory, NullLogger<ProviderRepository>.Instance);
        var provider = await repo.CreateProviderAsync("p", "https://x/v1", "k");

        var m1 = await repo.AddModelAsync(provider.Id, "glm-4-flash", null);
        var m2 = await repo.AddModelAsync(provider.Id, "glm-4-air", "空气");

        Assert.Single(await repo.ListModelsAsync(provider.Id), m => m.IsDefault == 1);
        Assert.Equal(m1.Id, (await repo.GetDefaultModelAsync(provider.Id))!.Id);

        Assert.True(await repo.SetDefaultModelAsync(provider.Id, m2.Id));
        Assert.Equal(m2.Id, (await repo.GetDefaultModelAsync(provider.Id))!.Id);

        // 删掉默认项后，剩下的第一个顶上（不变量：每个供应商恒有一个默认）
        Assert.True(await repo.DeleteModelAsync(m2.Id));
        Assert.Equal(m1.Id, (await repo.GetDefaultModelAsync(provider.Id))!.Id);
        Assert.False(await repo.DeleteModelAsync(m2.Id)); // 已删，第二次返回 false
    }

    // ── Agent runs ──────────────────────────────────────────

    [Fact]
    public async Task 运行记录自增回填且可按会话过滤()
    {
        var repo = new AgentRunRepository(_factory, NullLogger<AgentRunRepository>.Instance);

        var runA = await repo.InsertRunAsync(new AgentRunRecord
        {
            SessionId = "s1", Status = "succeeded", Iterations = 2, Model = "glm-4-flash",
            LatencyMs = 1200, CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        });
        var runB = await repo.InsertRunAsync(new AgentRunRecord
        {
            SessionId = "s2", Status = "failed", Iterations = 1, Model = "glm-4-flash",
            Error = "boom", ErrorCode = "model_timeout", CreatedAt = DateTimeOffset.UtcNow.ToString("o")
        });

        Assert.True(runA > 0 && runB > runA, "自增 id 必须回填且递增");

        await repo.InsertStepsAsync(runA, [
            new AgentStepRecord { Iteration = 1, Kind = "tool", Tool = "calc", Arguments = "{}", Result = "4" },
            new AgentStepRecord { Iteration = 2, Kind = "answer", Result = "42" }
        ]);

        var steps = await repo.GetStepsAsync(runA);
        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.Equal(runA, s.RunId)); // RunId 由方法回填
        Assert.Equal("answer", steps[1].Kind);               // 按 iteration 排序

        Assert.Single(await repo.QueryRunsAsync("s1", null, 10));
        Assert.Equal(2, (await repo.QueryRunsAsync(null, null, 10)).Count);
        Assert.Single(await repo.QueryRunsAsync(null, "failed", 10));
        Assert.Equal(runB, (await repo.QueryRunsAsync(null, "failed", 10))[0].Id); // 最近优先
        Assert.Equal("model_timeout", (await repo.GetRunAsync(runB))!.ErrorCode);

        // 空串是「过滤出 session_id = ''」，不是「不过滤」——与原 SQL 的 (@x IS NULL OR ...) 一致
        Assert.Empty(await repo.QueryRunsAsync("", null, 10));
    }

    // ── 基础设施 ────────────────────────────────────────────

    private static void MigrateUp(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddSQLite()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(PersistenceServiceCollectionExtensions).Assembly).For.Migrations());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* SQLite 句柄未释放时留给系统清理 */ }
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class FakeEnv(string root) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "AgentWorkbench.DataIntegrationTests";
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
