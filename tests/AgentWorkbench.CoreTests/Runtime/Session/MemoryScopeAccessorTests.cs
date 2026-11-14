using AgentWorkbench.Runtime.Session;
using Xunit;

namespace AgentWorkbench.CoreTests.Runtime.Session;

/// <summary>
/// 记忆作用域（ambient）：按 run 绑定、可嵌套、并发 run 之间互不串。
///
/// 这块此前没有测试，但它是「模型能不能看到别人会话记忆」的边界：
/// 作用域一旦串了，<c>remember</c> 会把 A 会话的偏好写到 B 名下。
/// </summary>
public sealed class MemoryScopeAccessorTests
{
    private static readonly MemoryScope Outer = MemoryScope.Session("outer");
    private static readonly MemoryScope Inner = MemoryScope.Session("inner");

    // [S1] Begin 期间 Current 是设定值；释放后回到「不在任何 run 内」（null）。
    // 负控：Begin 只赋值不返回恢复句柄（Dispose 空实现）→ 第二条断言红。
    [Fact]
    public void S1_Begin期间有值_释放后回到null()
    {
        var accessor = new AsyncLocalMemoryScopeAccessor();

        Assert.Null(accessor.Current);
        using (accessor.Begin(Outer))
            Assert.Equal(Outer, accessor.Current);

        Assert.Null(accessor.Current);
    }

    // [S2] 嵌套按栈恢复：内层释放回到外层，外层释放才回到 null。
    // 负控：Dispose 里直接置 null（不还原 previous）→ 内层释放后 Current 变 null，第一条断言红。
    [Fact]
    public void S2_嵌套按栈恢复()
    {
        var accessor = new AsyncLocalMemoryScopeAccessor();

        using (accessor.Begin(Outer))
        {
            using (accessor.Begin(Inner))
                Assert.Equal(Inner, accessor.Current);

            Assert.Equal(Outer, accessor.Current);
        }

        Assert.Null(accessor.Current);
    }

    // [S3] 重复 Dispose 幂等：不能把已经恢复好的外层值再改掉。
    // 负控：去掉 Interlocked 守卫 → 第二次 Dispose 会把外层的值写成内层释放前的 previous（null），红。
    [Fact]
    public void S3_重复Dispose不破坏外层()
    {
        var accessor = new AsyncLocalMemoryScopeAccessor();

        using (accessor.Begin(Outer))
        {
            var handle = accessor.Begin(Inner);
            handle.Dispose();
            handle.Dispose();

            Assert.Equal(Outer, accessor.Current);
        }

        Assert.Null(accessor.Current);
    }

    // [S4] 并发 run 各在自己的异步上下文里设作用域，互不覆盖——这是用 AsyncLocal 而不是
    // 普通字段的全部理由。
    // 负控：把 AsyncLocal 换成私有字段 → 后开始的 run 覆盖前者，结果两条都变成同一个 id，红。
    [Fact]
    public async Task S4_并发run的作用域互不串()
    {
        var accessor = new AsyncLocalMemoryScopeAccessor();

        async Task<string?> RunAsync(string id, int delayMs)
        {
            using var _ = accessor.Begin(MemoryScope.Session(id));
            await Task.Delay(delayMs);
            return accessor.Current?.Id;
        }

        // 故意让两者交错：慢的先设、快的后设，若用共享字段必然串。
        var results = await Task.WhenAll(RunAsync("slower", 30), RunAsync("faster", 1));

        Assert.Equal(new string?[] { "slower", "faster" }, results);
        Assert.Null(accessor.Current);
    }
}
