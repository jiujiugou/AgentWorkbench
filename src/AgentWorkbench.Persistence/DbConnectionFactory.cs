using Microsoft.Data.Sqlite;

namespace AgentWorkbench.Persistence;

public interface IDbConnectionFactory
{
    SqliteConnection Create();
}

/// <summary>创建已打开的 SQLite 连接，开启 WAL 与外键约束。</summary>
public sealed class SqliteDbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqliteDbConnectionFactory(StoragePaths paths) => _connectionString = paths.ConnectionString;

    public SqliteConnection Create()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }
}
