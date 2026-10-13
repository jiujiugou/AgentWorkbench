using Microsoft.Extensions.Options;

namespace AgentWorkbench.UnitTests.Tools;

/// <summary>
/// 可变的 <see cref="IOptionsMonitor{T}"/> 测试替身：<see cref="Update"/> 模拟配置源热重载后的新值。
/// 白名单（<c>IToolPolicy</c>）与审批级别（<c>IToolApprovalPolicy</c>）两条热重载链路共用它，
/// 免得每个用例各写一份替身、各自漂移。
/// </summary>
internal sealed class MutableOptionsMonitor<T>(T initial) : IOptionsMonitor<T>
{
    private T _current = initial;

    public T CurrentValue => _current;

    public T Get(string? name) => _current;

    public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();

    public void Update(T value) => _current = value;

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
