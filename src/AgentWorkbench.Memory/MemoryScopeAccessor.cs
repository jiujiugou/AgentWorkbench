namespace AgentWorkbench.Memory;

/// <summary>
/// 当前 run 的记忆作用域（ambient）。记忆工具在被模型调用时读它，才知道「这条记忆记在谁名下」。
/// </summary>
/// <remarks>
/// 为什么不让模型用参数指定 scope_id：那等于把隐私边界交给模型。作用域由编排层按当前 run 设置，
/// 模型只能选「本作用域 / 全局」这种粗粒度开关。
/// </remarks>
public interface IMemoryScopeAccessor
{
    /// <summary>当前作用域；不在任何 run 内（未设置）时为 <c>null</c>。</summary>
    MemoryScope? Current { get; }

    /// <summary>把作用域设为 <paramref name="scope"/>，直到返回的句柄被释放（可嵌套，按栈恢复）。</summary>
    IDisposable Begin(MemoryScope scope);
}

/// <summary>
/// 用 <see cref="AsyncLocal{T}"/> 承载作用域的默认实现：随异步流程流动，
/// 因此并发 run 之间互不串扰（各自在自己的 async 上下文里设置）。
/// </summary>
public sealed class AsyncLocalMemoryScopeAccessor : IMemoryScopeAccessor
{
    // 实例字段而不是 static：多个实例（如测试里各建一个）之间不互相污染。
    private readonly AsyncLocal<MemoryScope?> _ambient = new();

    public MemoryScope? Current => _ambient.Value;

    public IDisposable Begin(MemoryScope scope)
    {
        var previous = _ambient.Value;
        _ambient.Value = scope;
        return new Restore(_ambient, previous);
    }

    private sealed class Restore(AsyncLocal<MemoryScope?> ambient, MemoryScope? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // 幂等：重复 Dispose 不能把外层的值改坏。
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                ambient.Value = previous;
        }
    }
}
