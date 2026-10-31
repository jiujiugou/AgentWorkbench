using AgentWorkbench.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentWorkbench.DataIntegrationTests;

/// <summary>
/// SQLite 的 foreign_keys 是连接级开关且默认关闭。Dapper 路径靠
/// <c>SqliteDbConnectionFactory.Create()</c> 里的 PRAGMA 打开；EF 自己建连时不会执行那条 PRAGMA，
/// 必须由连接串的 <c>Foreign Keys=True</c> 保证。
/// </summary>
/// <remarks>
/// 这条用例是级联删除的安全带：连接串一旦被改成不含 Foreign Keys，
/// 「删 documents 连带删 chunks」会静默失效——不报错、不抛异常，只是子行留下。
/// 本用例会因此立刻变红。
/// </remarks>
public sealed class SqliteForeignKeysTests
{
    [Fact]
    public async Task EF连接开启外键_级联删除生效()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"aw-fk-{Guid.NewGuid():N}.db");
        try
        {
            // 与 StoragePaths 生成的连接串保持一致的一个关键参数
            var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                ForeignKeys = true,
                Pooling = false // 临时库：让连接关闭即释放文件，否则 finally 里删不掉
            }.ToString();

            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options);

            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE parents (id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE children (
                    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    parent_id TEXT NOT NULL REFERENCES parents(id) ON DELETE CASCADE);
                """);

            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO parents (id) VALUES ('p1'); INSERT INTO children (parent_id) VALUES ('p1');");

            await db.Database.ExecuteSqlRawAsync("DELETE FROM parents WHERE id = 'p1';");

            var remaining = await db.Database
                .SqlQuery<int>($"SELECT COUNT(1) AS \"Value\" FROM children")
                .SingleAsync();

            Assert.Equal(0, remaining);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = dbPath + suffix;
                if (File.Exists(file))
                    File.Delete(file);
            }
        }
    }
}
