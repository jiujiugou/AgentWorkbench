using AgentWorkbench.Documents;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using Moq;
using AgentWorkbench.Persistence;
using AgentWorkbench.Shared.Options;
using AgentWorkbench.Webapi.Controllers;
using FluentMigrator.Runner;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentWorkbench.ApiContractTests.Support;

/// <summary>
/// F2 契约验收台：真 SQLite（跑真迁移）+ 真仓储 + 真摄入服务 + 真 Controller，
/// 只把「知识引擎」和「解析队列」换成替身——它们各自是外部依赖与异步边界，不属于本域契约。
/// </summary>
/// <remarks>
/// 每个实例一套独立临时目录，互不干扰。用 Controller 直调而非真 HTTP：
/// 契约里除 413（请求体上限由 Kestrel 拦）之外都能在这一层观察；413 需真管道，另行验证。
/// </remarks>
public sealed class F2Harness : IDisposable
{
    public string Root { get; }
    public StorageOptions Options { get; }
    public IDocumentRepository Repository { get; }
    public FakeParseQueue Queue { get; } = new();
    public Mock<IKnowledgeEngine> Knowledge { get; } = new();
    public DocumentsController Controller { get; }
    public string UploadsDir => Options.UploadsDir!;

    public F2Harness(long maxFileBytes = 30L * 1024 * 1024)
    {
        Root = Path.Combine(Path.GetTempPath(), "f2-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);

        Options = new StorageOptions
        {
            DataDir = Path.Combine(Root, "data"),
            UploadsDir = Path.Combine(Root, "data", "uploads"),
            DbPath = Path.Combine(Root, "data", "f2.db"),
            MaxFileBytes = maxFileBytes,
            MaxPages = 200,
        };

        var paths = new StoragePaths(new FakeHostEnvironment(Root), Microsoft.Extensions.Options.Options.Create(Options));
        MigrateUp(paths);

        var factory = new HarnessContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(paths.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
        Repository = new DocumentRepository(factory, NullLogger<DocumentRepository>.Instance);

        var ingestion = new DocumentIngestionService(
            Repository, Queue, paths,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<DocumentIngestionService>.Instance);

        Controller = new DocumentsController(
            Repository, ingestion, Queue, Knowledge.Object,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<DocumentsController>.Instance);
    }

    private static void MigrateUp(StoragePaths paths)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddSQLite()
                .WithGlobalConnectionString(paths.ConnectionString)
                .ScanIn(typeof(PersistenceServiceCollectionExtensions).Assembly).For.Migrations());

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    // ── 请求 ──────────────────────────────────────────────

    /// <summary>上传；<paramref name="content"/> 为 null 表示请求里根本没有 file 字段。</summary>
    public Task<IActionResult> UploadAsync(string fileName, byte[]? content) =>
        Controller.Upload(
            content is null ? null! : FormFile(fileName, content),
            CancellationToken.None);

    public static IFormFile FormFile(string fileName, byte[] content) =>
        new FormFile(new MemoryStream(content), 0, content.Length, "file", fileName);

    // ── 观察 ──────────────────────────────────────────────

    /// <summary>盘上文件数 / 库里记录数 / 队列条数——判断「什么都没发生」用这三项。</summary>
    public async Task<(int Files, int Docs, int Queued)> SnapshotAsync()
    {
        var files = Directory.Exists(UploadsDir)
            ? Directory.GetFiles(UploadsDir, "*", SearchOption.AllDirectories).Length
            : 0;
        var docs = (await Repository.ListAsync()).Count;
        return (files, docs, Queue.Count);
    }

    public static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult obj => obj.StatusCode ?? 200,
        StatusCodeResult status => status.StatusCode,
        _ => 200,
    };

    /// <summary>取响应体里的 <c>code</c> 字段；没有就是 null（现在 4 条校验测试红在这里）。</summary>
    public static string? CodeOf(IActionResult result)
    {
        if (result is not ObjectResult { Value: { } body })
            return null;

        return body.GetType().GetProperty("code")?.GetValue(body) as string;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* SQLite WAL 句柄未释放时留给系统清理 */ }
    }
}

internal sealed class HarnessContextFactory(DbContextOptions<AppDbContext> options)
    : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());
}

internal sealed class FakeHostEnvironment : IHostEnvironment
{
    public FakeHostEnvironment(string contentRoot)
    {
        ContentRootPath = contentRoot;
        ContentRootFileProvider = new NullFileProvider();
    }

    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "AgentWorkbench.ApiContractTests";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}

/// <summary>解析队列替身：只记录入队了几条，不真跑解析。</summary>
public sealed class FakeParseQueue : IDocumentParseQueue
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public ValueTask EnqueueAsync(string documentId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
