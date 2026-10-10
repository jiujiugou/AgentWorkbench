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
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private static string Resolve(IHostEnvironment environment, string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
}
