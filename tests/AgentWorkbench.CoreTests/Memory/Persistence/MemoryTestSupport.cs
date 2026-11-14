using AgentWorkbench.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AgentWorkbench.CoreTests.Memory.Persistence;

/// <summary>
/// ⑤ 状态与记忆测试的共用件：临时库 + 可控时钟。
///
/// 建表读生产同一份 Schema（<see cref="MemoryItemsSchema"/> / <see cref="AgentSessionsSchema"/> /
/// <see cref="ChatHistorySchema"/>），不在测试里另抄一份 DDL——抄了就会漂，
/// 而且是「测试绿、生产崩」那种最难查的漂。
/// </summary>
internal sealed class TempDatabase : IDisposable
{
    static TempDatabase()
    {
        // 与生产 AddNitroPersistence 一致：Dapper 全局开启下划线列映射。
        // 少了这一步，use_count / last_used_at / created_at 这类列全部映射不上，
        // 读回的对象字段全是默认值——而且现象很隐蔽（单单词列如 id/content 是对的）。
        // 这正是本文件第一次写测试时踩到的：写进去 1、读回来 0。
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private readonly string _dir;

    public string ConnectionString { get; }

    public IDbConnectionFactory Factory { get; }

    private TempDatabase(string dir, string connectionString)
    {
        _dir = dir;
        ConnectionString = connectionString;
        Factory = new TempConnectionFactory(connectionString);
    }

    /// <summary>建一个临时库，按传入顺序执行各 Schema 的语句。</summary>
    public static TempDatabase Create(params IReadOnlyList<string>[] schemas)
    {
        var dir = Path.Combine(Path.GetTempPath(), "aw-mem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dir, "memory.db"),
            DefaultTimeout = 30,
        }.ToString();

        var db = new TempDatabase(dir, connectionString);
        using var connection = db.Factory.Create();
        foreach (var statement in schemas.SelectMany(x => x))
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }

        return db;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* 临时目录清理失败不影响结论 */ }
    }

    private sealed class TempConnectionFactory(string connectionString) : IDbConnectionFactory
    {
        public SqliteConnection Create()
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            // 与生产 SqliteDbConnectionFactory 一致：WAL + 外键。
            // 少了 WAL，shared-cache 下的读连接会看到旧快照，写进去的东西读不回来。
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
            return connection;
        }
    }
}

/// <summary>
/// 可控时钟：保留期与过期清理都按「现在」判，测试必须能把时间推过去，
/// 否则只能靠 <c>Thread.Sleep</c> 等真实时间——慢且不稳。
/// </summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
