using AgentWorkbench.Shared.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Persistence;

/// <summary>解析后的绝对存储路径与 SQLite 连接串。</summary>
public sealed class StoragePaths
{
    public string DataDir { get; }
    public string UploadsDir { get; }
    public string DbPath { get; }
    public string ConnectionString { get; }

    public StoragePaths(IHostEnvironment environment, IOptions<StorageOptions> options)
    {
        var value = options.Value;
        DataDir = Resolve(environment, value.DataDir);
        UploadsDir = Resolve(environment, value.UploadsDir);
        DbPath = Resolve(environment, value.DbPath);

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(UploadsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Cache = SqliteCacheMode.Shared,
            // foreign_keys 是连接级开关且默认关闭。SqliteDbConnectionFactory 每次建连都执行
            // PRAGMA foreign_keys=ON，但 EF 自己建连时不走那条路径 → 级联删除会静默失效
            // （删 documents 不再连带 chunks / document_pages）。写进连接串，两条路径都生效。
            ForeignKeys = true
        }.ToString();
    }

    private static string Resolve(IHostEnvironment environment, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
}
