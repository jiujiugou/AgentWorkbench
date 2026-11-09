using AgentWorkbench.ActionGateway;
using AgentWorkbench.Persistence;
using AgentWorkbench.Persistence.ActionStore;
using AgentWorkbench.Ports;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentWorkbench.ApplicationTests.ActionGateway;

/// <summary>
/// 落库后的验收：重启不丢、并发唯一、fail-closed 不变。
/// 每个用例用独立临时库，互不干扰；新实例指向同一文件即模拟「进程重启」。
/// </summary>
public sealed class SqliteActionStoreTests
{
    private sealed class FileConnections : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public FileConnections(string path)
        {
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 30,
            }.ToString();
        }

        public SqliteConnection Create()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
            return connection;
        }
    }

    private static (IDbConnectionFactory Connections, Action Cleanup) NewDatabase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aw-action-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var connections = new FileConnections(Path.Combine(dir, "actions.db"));
        return (connections, () =>
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* 临时目录清理失败不影响结论 */ }
        });
    }

    private static ActionRunService Service(
        IActionSet set, IDbConnectionFactory connections, out SqliteTraceStore trace)
    {
        trace = new SqliteTraceStore(connections);
        return new ActionRunService(set, trace, new SqliteActionRunStore(connections), trace);
    }

    [Fact]
    public async Task S1_重启后终态与审计轨迹仍在()
    {
        var (connections, cleanup) = NewDatabase();
        try
        {
            var set = new TestActionSet();
            var service = Service(set, connections, out _);
            var result = await service.RunAsync(TestRequests.Create(requestId: "restart-1"), CancellationToken.None);
            Assert.Equal(ActionStatuses.Succeeded, result.Status);

            // 全新实例 = 进程重启，进程内什么都没有。
            var reloadedStore = new SqliteActionRunStore(connections);
            var reloadedTrace = new SqliteTraceStore(connections);
            var reloaded = await reloadedStore.LoadResultAsync("restart-1", CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.Equal(ActionStatuses.Succeeded, reloaded!.Status);
            Assert.Equal("EXT-0001", reloaded.ExternalId);

            var trail = await reloadedTrace.AuditTrailAsync("restart-1", CancellationToken.None);
            Assert.NotEmpty(trail);
            Assert.Contains(trail, e => e.Stage == AuditStages.Execute);
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task S2_并发同请求号只有一次领取成功()
    {
        // 负控：把 INSERT OR IGNORE 换成「先查再插」→ 高并发下本条会红。
        var (connections, cleanup) = NewDatabase();
        try
        {
            var store = new SqliteActionRunStore(connections);
            var claims = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => store.TryClaimIdempotencyAsync("same-id", null, CancellationToken.None).AsTask()));
            Assert.Equal(1, claims.Count(claimed => claimed));
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task S3_第二次同请求号回放且执行体只跑一次()
    {
        var (connections, cleanup) = NewDatabase();
        try
        {
            var set = new TestActionSet();
            var service = Service(set, connections, out _);
            var first = await service.RunAsync(TestRequests.Create("idem-1"), CancellationToken.None);
            var second = await service.RunAsync(TestRequests.Create("idem-1"), CancellationToken.None);

            Assert.Equal(ActionStatuses.Succeeded, first.Status);
            Assert.Equal(ActionSuccessCodes.ReplayedFromIdempotency, second.Code);
            Assert.Equal(1, set.ExecutorStub.WriteCallCount);
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task S4_轨迹写不进去时网关拒绝且零外部调用()
    {
        var (connections, cleanup) = NewDatabase();
        try
        {
            var set = new TestActionSet();
            var trace = new SqliteTraceStore(connections) { Available = false };
            var service = new ActionRunService(set, trace, new SqliteActionRunStore(connections), trace);

            var result = await service.RunAsync(TestRequests.Create("audit-fail"), CancellationToken.None);

            Assert.Equal(ActionStatuses.Rejected, result.Status);
            Assert.Equal(ActionRejectionCodes.AuditWriteFailed, result.Code);
            Assert.Equal(0, set.ExecutorStub.CallCount);
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task S5_挂起的待审请求跨重启仍能恢复执行()
    {
        var (connections, cleanup) = NewDatabase();
        try
        {
            var set = new TestActionSet();
            var first = Service(set, connections, out _);
            var pending = await first.SubmitDeferredAsync(
                TestRequests.Create("pending-1"), CancellationToken.None);
            Assert.Equal(ActionStatuses.Pending, pending.Status);
            Assert.Equal(0, set.ExecutorStub.CallCount);

            var afterRestart = Service(set, connections, out _);
            var approved = await afterRestart.ApproveAsync("pending-1", "owner-wang", CancellationToken.None);
            Assert.NotNull(approved);
            Assert.Equal(ActionStatuses.Succeeded, approved!.Status);
            Assert.Equal(1, set.ExecutorStub.WriteCallCount);
        }
        finally { cleanup(); }
    }

    [Fact]
    public async Task S6_并发提交同一请求号只产生一次外部副作用()
    {
        var (connections, cleanup) = NewDatabase();
        try
        {
            var set = new TestActionSet();
            var service = Service(set, connections, out _);
            await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => service.RunAsync(TestRequests.Create("race-1"), CancellationToken.None).AsTask()));

            Assert.Equal(1, set.ExecutorStub.WriteCallCount);
            // 其余并发拿到的是已落库的既有终态（当前语义不额外打 replayed 标记），
            // 关键是「只有一次外部副作用」。再用全新实例确认去重记录确实落了库、不是进程内状态。
            var afterRestart = new SqliteActionRunStore(connections);
            Assert.False(await afterRestart.TryClaimIdempotencyAsync("race-1", null, CancellationToken.None));
        }
        finally { cleanup(); }
    }
}
