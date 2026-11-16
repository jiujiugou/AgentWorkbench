using System.Reflection;
using System.Text.Json;
using AgentWorkbench.Runtime.Session;
using Microsoft.Agents.AI;
using Moq;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// S05 之外的会话契约装置：<c>SessionContractTests</c>（F4.5 / F4.6 / F4.7）用的两个替身。
/// </summary>
/// <remarks>
/// 这两个类型原本定义在 <c>SessionStoreTests</c> 里 —— 同程序集时可以直接借。
/// 本轮 <c>SessionStoreTests</c> 已按「Core 模块自己的行为归 CoreTests」搬走，
/// 而本文件所属的那组判据（默认值必须关）留在 ApplicationTests，于是把替身留在这里。
/// 两侧各有一份，是搬家造成的，不是又开了一套判据。
/// </remarks>
internal sealed class CountingSessionFactory : ISessionFactory
{
    public int CreateCount { get; private set; }
    public int SerializeCount { get; private set; }
    public int DeserializeCount { get; private set; }
    public bool ThrowOnSerialize { get; set; }
    public bool ThrowOnDeserialize { get; set; }
    public List<AgentSession> Created { get; } = new();

    public ValueTask<AgentSession> CreateAsync(CancellationToken cancellationToken)
    {
        var session = new Mock<AgentSession>().Object;
        CreateCount++;
        Created.Add(session);
        return ValueTask.FromResult(session);
    }

    public ValueTask<JsonElement> SerializeAsync(AgentSession session, CancellationToken cancellationToken)
    {
        SerializeCount++;
        if (ThrowOnSerialize)
            throw new InvalidOperationException("快照序列化失败（模拟损坏 / 版本不识别）");
        using var document = JsonDocument.Parse("{\"marker\":\"ok\"}");
        return ValueTask.FromResult(document.RootElement.Clone());
    }

    public ValueTask<AgentSession> DeserializeAsync(JsonElement state, CancellationToken cancellationToken)
    {
        DeserializeCount++;
        if (ThrowOnDeserialize)
            throw new InvalidOperationException("快照反序列化失败（模拟损坏 / 版本不识别）");
        return ValueTask.FromResult(new Mock<AgentSession>().Object);
    }
}

/// <summary>
/// 只读观察 SessionStore 内部缓存（反射读 private 字段，不构造对象、不改生产代码）。
/// 字段与属性名来自 SessionStore.cs:50（<c>Entry(AgentSession Session, DateTimeOffset LastAccess)</c>）
/// 与 :52（<c>private readonly ConcurrentDictionary&lt;string, Entry&gt; _sessions</c>）。
/// </summary>
internal static class SessionStoreProbe
{
    private static object Dictionary(SessionStore store)
        => typeof(SessionStore)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(store)!;

    /// <summary>当前缓存的 sessionId 集合（按序数排序，便于断言具体值）。</summary>
    public static string[] Ids(SessionStore store)
    {
        var dict = Dictionary(store);
        var keys = (IEnumerable<string>)dict.GetType().GetProperty("Keys")!.GetValue(dict)!;
        return keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    public static int Count(SessionStore store)
    {
        var dict = Dictionary(store);
        return (int)dict.GetType().GetProperty("Count")!.GetValue(dict)!;
    }

    /// <summary>读取某个 id 的 Entry（会话实例与最后访问时间）。</summary>
    public static (AgentSession Session, DateTimeOffset LastAccess) Entry(SessionStore store, string id)
    {
        var dict = Dictionary(store);
        var entry = dict.GetType().GetProperty("Item")!.GetValue(dict, new object[] { id })!;
        var session = (AgentSession)entry.GetType().GetProperty("Session")!.GetValue(entry)!;
        var last = (DateTimeOffset)entry.GetType().GetProperty("LastAccess")!.GetValue(entry)!;
        return (session, last);
    }
}
