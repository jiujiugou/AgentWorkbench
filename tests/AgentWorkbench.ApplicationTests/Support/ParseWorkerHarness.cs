using System.Runtime.CompilerServices;
using AgentWorkbench.Documents;
using AgentWorkbench.Documents.Parsing;
using AgentWorkbench.Domain;
using AgentWorkbench.Knowledge.Abstractions;
using AgentWorkbench.Persistence;
using AgentWorkbench.Shared.Options;
using FluentMigrator.Runner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AgentWorkbench.ApplicationTests.Support;

/// <summary>
/// 功能 2 的 Worker 验收台：真 SQLite（跑真迁移）+ 真仓储 + 真文本抽取/清洗/切块，
/// 只把解析队列、PDF 抽取器和知识引擎换成替身。
/// </summary>
/// <remarks>
/// 用例编号对应 docs/验证与现状/F2/验证方式.md §7（S06–S11）。
/// Worker 是 BackgroundService，测试用「入队 → StartAsync → 轮询终态 → StopAsync」驱动。
/// </remarks>
public sealed class ParseWorkerHarness : IDisposable
{
    public string Root { get; }
    public StorageOptions Options { get; }
    public StoragePaths Paths { get; }
    public IDocumentRepository Repository { get; }
    public Mock<IKnowledgeEngine> Knowledge { get; } = new();
    public Mock<IPdfTextExtractor> PdfExtractor { get; } = new();

    public ITextFileExtractor TextExtractor { get; } = new TextFileExtractor();
    public ITextCleaner Cleaner { get; } = new TextCleaner(NullLogger<TextCleaner>.Instance);
    public IChunker Chunker { get; } = new Chunker(NullLogger<Chunker>.Instance);

    public ParseWorkerHarness(int maxPages = 200)
    {
        Root = Path.Combine(Path.GetTempPath(), "f2-worker-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);

        Options = new StorageOptions
        {
            DataDir = Path.Combine(Root, "data"),
            UploadsDir = Path.Combine(Root, "data", "uploads"),
            DbPath = Path.Combine(Root, "data", "worker.db"),
            MaxFileBytes = 30L * 1024 * 1024,
            MaxPages = maxPages,
        };

        Paths = new StoragePaths(
            new FakeHostEnvironment(Root),
            Microsoft.Extensions.Options.Options.Create(Options));
        MigrateUp(Paths.ConnectionString);

        var factory = new HarnessContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Paths.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);
        Repository = new DocumentRepository(factory, NullLogger<DocumentRepository>.Instance);

        Knowledge
            .Setup(k => k.RemoveDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemoveResult(0, 0, null));
        Knowledge
            .Setup(k => k.IndexDocumentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IndexResult(1, 1, 1, null));
    }

    public async Task<DocumentRecord> InsertAsync(
        string fileName, byte[]? content, bool createFile = true)
    {
        var id = Guid.NewGuid().ToString("N");
        var storedPath = Path.Combine(Paths.UploadsDir, id + Path.GetExtension(fileName));
        if (createFile)
            await File.WriteAllBytesAsync(storedPath, content ?? []);

        var document = new DocumentRecord
        {
            Id = id,
            FileName = fileName,
            StoredPath = storedPath,
            Sha256 = id,
            SizeBytes = content?.Length ?? 0,
            Status = DocumentStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        await Repository.InsertAsync(document);
        return document;
    }

    /// <summary>驱动 Worker 处理一条记录，返回终态；5s 未到终态即判失败。</summary>
    public async Task<DocumentRecord> ProcessAsync(string documentId)
    {
        var worker = CreateWorker(new OneShotQueue(documentId));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var document = await Repository.GetAsync(documentId);
                if (document?.Status is DocumentStatus.Parsed or DocumentStatus.Failed)
                    return document;

                await Task.Delay(20);
            }

            var last = await Repository.GetAsync(documentId);
            throw new TimeoutException(
                $"Worker 未在 5s 内到达终态，当前状态：{last?.Status ?? "(记录不存在)"}");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private DocumentParseWorker CreateWorker(IDocumentParseQueue queue) => new(
        queue,
        Repository,
        PdfExtractor.Object,
        TextExtractor,
        Cleaner,
        Chunker,
        Knowledge.Object,
        Microsoft.Extensions.Options.Options.Create(Options),
        NullLogger<DocumentParseWorker>.Instance);

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
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* SQLite WAL 句柄未释放时留给系统清理 */ }
    }
}

internal sealed class OneShotQueue(string documentId) : IDocumentParseQueue
{
    private int _read;

    public ValueTask EnqueueAsync(string id, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public async IAsyncEnumerable<string> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (Interlocked.Exchange(ref _read, 1) == 0)
            yield return documentId;
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
    public string ApplicationName { get; set; } = "AgentWorkbench.ApplicationTests";
    public string ContentRootPath { get; set; }
    public IFileProvider ContentRootFileProvider { get; set; }
}
