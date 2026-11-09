using System.Collections.Concurrent;

namespace AgentWorkbench.Runtime.Session;

/// <summary>
/// 会话级运行互斥：同一个 <c>sessionId</c> 同一时刻只允许一个 run 在推进。
///
/// 为什么需要它：历史改为逐条追加之后，「后写覆盖先写」不再造成数据丢失，
/// 但两个 run 仍会把各自的对话交错写进同一会话——模型会在半截上下文里继续推理，
/// 审批与幂等判断也建立在这段错乱的上下文上。追加解决了「丢」，没解决「乱」。
///
/// 语义是<b>拒绝</b>而不是排队：排队没有上限，第二句话可能几分钟后才被处理，
/// 用户早已忘了上下文。抢不到时调用方应把 <c>session_busy</c> 如实回给用户。
///
/// <b>覆盖范围只有单进程。</b>锁在内存里，跨进程 / 跨机不成立；
/// 多实例部署需要数据库租约（session_leases + fence），见 docs/core/runtime.md §7.6。
/// 进程崩溃时锁随进程消失，不会留下僵尸会话——这是相对数据库租约的唯一优势。
/// </summary>
public interface ISessionRunGate
{
    /// <summary>
    /// 尝试占用会话。抢到返回句柄（<c>DisposeAsync</c> 即释放），已被占用返回 <c>null</c>。
    /// </summary>
    /// <param name="sessionId">会话标识；调用方保证非空。</param>
    ValueTask<IAsyncDisposable?> TryAcquireAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// 用 <c>ConcurrentDictionary.TryAdd</c> 而不是 <c>SemaphoreSlim</c>：
/// 这里不需要等待（拒绝语义），只需要一个原子的「占位 / 让位」。
///
/// 释放走 <see cref="Interlocked"/>&#160;+&#160;<c>TryRemove(KeyValuePair)</c> 两重保护，见 <c>Handle.DisposeAsync</c>。
/// </remarks>
public sealed class SessionRunGate : ISessionRunGate
{
    private readonly ConcurrentDictionary<string, byte> _busy = new(StringComparer.Ordinal);

    public ValueTask<IAsyncDisposable?> TryAcquireAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (!_busy.TryAdd(sessionId, 0))
            return ValueTask.FromResult<IAsyncDisposable?>(null);

        return ValueTask.FromResult<IAsyncDisposable?>(new Handle(_busy, sessionId));
    }

    private sealed class Handle(ConcurrentDictionary<string, byte> busy, string sessionId) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            // 幂等：重复 Dispose 不得删掉别人的占位。
            //
            // _released 是主保护——负控证明：去掉它、每次 Dispose 都 TryRemove(key)，
            // 已释放的句柄会把后来者的锁一起删掉（SessionRunGateTests.G3 变红）。
            // TryRemove(KeyValuePair) 是防御性冗余：把 key 和值一起比对，
            // 即使将来有人去掉 _released，也不会误删别人的占位。
            if (Interlocked.Exchange(ref _released, 1) == 0)
                busy.TryRemove(new KeyValuePair<string, byte>(sessionId, 0));

            return ValueTask.CompletedTask;
        }
    }
}
