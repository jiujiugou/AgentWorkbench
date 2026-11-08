using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AgentWorkbench.Runtime.Session;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// F4.5 / F4.6 / F4.7：会话契约 —— 快照损坏降级、两臂同契约、长期记忆默认值。
///
/// 【预期红：红是正确信号】——仅针对 F4.7：当前 MemoryOptions.LongTermEnabled 默认 true
/// （MemoryOptions.cs:23），且工具注册表 fallback 也是 ?? true
/// （MemoryPersistenceServiceCollectionExtensions.cs:38），与 ADR 0016 §五 A5 / B7
/// 「长期记忆停用不删」相反。F4.7 断言「默认应为 false」，必然红；红是正确的验收信号，
/// 不要改生产代码把它改绿。F4.5 / F4.6 的断言预期绿。
///
/// 【F4.5 / F4.6 的退化说明】测试项目 AgentWorkbench.ApplicationTests.csproj 未 ProjectReference
/// AgentWorkbench.Memory.Persistence，且本任务禁止改 .csproj / .slnx / src；因此编译期无法引用
/// SqliteSessionStore / ISessionStateRepository / SessionStateRecord，运行期该程序集也不在测试输出
/// 目录（无构建产物，无法反射加载）。按《运行时底座完成定义》§4.3 与任务规则 3 允许的方式退化为：
///   ① 同一套契约断言跑内存版 SessionStore 两次（两个独立实例，逐字段比对具体值）；
///   ② 对落库版源码做结构断言（读 SqliteSessionStore.cs 文本，验证降级分支与契约要素）。
/// 明确声明：没有运行过落库臂，未假装验了两臂。要真验两臂，需给测试 csproj 增加对
/// AgentWorkbench.Memory.Persistence 的 ProjectReference，再用内存 ISessionStateRepository 替身
/// 构造 SqliteSessionStore，把本文件 ① 的同一套用例喂给它。
///
/// 结构断言读取的是仓库内生产源码文本（从测试输出目录向上找仓库根），不是编译后的程序集；
/// 若 CI 只发布测试 dll 而不带源码，结构断言会因找不到文件而报错——这是退化方案的固有局限。
/// </summary>
public sealed class SessionContractTests
{
    /// <summary>从测试输出目录向上找仓库根，读取生产源码文本（仅结构断言用，不编译落库版）。</summary>
    private static string ReadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var parts = new List<string> { dir.FullName, "src", "Core" };
            parts.AddRange(relativeParts);
            var candidate = Path.Combine(parts.ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"结构断言需要读取生产源码 {string.Join("/", relativeParts)}；未从 {AppContext.BaseDirectory} 向上找到仓库根。",
            string.Join("/", relativeParts));
    }

    // [F4.5] 降级：快照损坏 / 版本不识别 → 新会话，不抛，且有降级痕迹
    // 负控（内存臂）：把 SessionStore.SaveAsync 改成调用 factory.SerializeAsync → 坏 factory 会抛，
    //       `Threw==false` / `SerializeCount==0` 红；把 GetOrCreateAsync 改成调用 DeserializeAsync
    //       → `DeserializeCount==0` 红。
    // 负控（落库臂·结构断言）：删掉 SqliteSessionStore.cs:56-61 的 `catch (Exception ex)` 降级分支、
    //       删掉 :59 的 LogWarning、或删掉 :60 的 DeleteAsync → 对应的 Contains / 顺序断言红；
    //       去掉 :50 的 RootElement.Clone() → Clone 断言红。
    // 【退化】未运行落库臂（原因见文件头）：内存臂只验「损坏快照对它无害」，落库臂只做结构断言；
    //       ③ 的反例对照用「快照正常」的 factory 跑同一套件，证明断言不是恒真。
    [Fact]
    public async Task F45_CorruptSnapshot_Degrades_WithoutThrowing()
    {
        // ① 同一套断言跑内存版两次（两个独立实例）
        var armA = await RunCorruptionToleranceSuite(corrupt: true);
        var armB = await RunCorruptionToleranceSuite(corrupt: true);
        Assert.Equal(armA.CreateCount, armB.CreateCount);
        Assert.Equal(armA.SerializeCount, armB.SerializeCount);
        Assert.Equal(armA.DeserializeCount, armB.DeserializeCount);
        Assert.Equal(armA.CachedIds, armB.CachedIds);
        Assert.Equal(armA.Threw, armB.Threw);

        // 具体值：坏 factory 下不抛、只新建 1 次、存储 1 条、完全不碰序列化 / 反序列化
        Assert.False(armA.Threw);
        Assert.Equal(1, armA.CreateCount);
        Assert.Equal(0, armA.SerializeCount);
        Assert.Equal(0, armA.DeserializeCount);
        Assert.Single(armA.CachedIds);
        Assert.Equal(new[] { "s-corrupt" }, armA.CachedIds);

        // ③ 反例对照：快照正常（不抛）的 factory 跑同一套件，结果完全一致
        //    → 内存臂根本不解析快照，故「损坏」与「正常」都免疫；不是「怎么跑都不抛」的恒真断言。
        var control = await RunCorruptionToleranceSuite(corrupt: false);
        Assert.False(control.Threw);
        Assert.Equal(control.CreateCount, armA.CreateCount);
        Assert.Equal(control.SerializeCount, armA.SerializeCount);
        Assert.Equal(control.DeserializeCount, armA.DeserializeCount);
        Assert.Equal(control.CachedIds, armA.CachedIds);

        // ② 落库臂结构断言：降级分支存在，且「解析 → 异常 → 留痕(日志) → 删行 → 新建」顺序正确
        var source = ReadSource("Memory", "AgentWorkbench.Memory.Persistence", "SqliteSessionStore.cs");
        Assert.Contains("JsonDocument.Parse(record.StateJson)", source);   // 解析快照
        Assert.Contains("document.RootElement.Clone()", source);           // Clone 独立于被释放的 document
        Assert.Contains("factory.DeserializeAsync(", source);               // 走 ISessionFactory 反序列化
        Assert.Contains("catch (OperationCanceledException)", source);      // 取消不被降级分支误吞
        Assert.Contains("catch (Exception ex)", source);                    // 降级分支
        Assert.Contains("_logger.LogWarning(ex,", source);                 // 降级痕迹：告警日志
        Assert.Contains("await _repository.DeleteAsync(sessionId, cancellationToken)", source); // 降级痕迹：删损坏行
        Assert.Contains("return await factory.CreateAsync(cancellationToken)", source);        // 降级后新建

        var catchAt = source.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        var warnAt = source.IndexOf("_logger.LogWarning(ex,", StringComparison.Ordinal);
        var deleteAt = source.LastIndexOf(
            "await _repository.DeleteAsync(sessionId, cancellationToken)", StringComparison.Ordinal);
        var createAt = source.IndexOf("return await factory.CreateAsync(cancellationToken)", StringComparison.Ordinal);
        Assert.True(catchAt >= 0 && warnAt > catchAt && deleteAt > warnAt && createAt > deleteAt,
            "降级顺序必须是：catch → LogWarning → DeleteAsync → 新建会话");
    }

    private sealed record CorruptionObservation(
        int CreateCount, int SerializeCount, int DeserializeCount, string[] CachedIds, bool Threw);

    /// <summary>
    /// 内存臂容错套件：corrupt=true 时 factory 的 Serialize/Deserialize 都抛（模拟快照损坏 / 版本不识别）。
    /// 内存版 SaveAsync 不调用 factory、GetOrCreateAsync 不反序列化，故损坏与否都不影响它。
    /// </summary>
    private static async Task<CorruptionObservation> RunCorruptionToleranceSuite(bool corrupt)
    {
        var store = new SessionStore(
            Options.Create(new MemoryOptions { SessionTtlMinutes = 0, MaxCachedSessions = 0 }),
            TimeProvider.System);
        var factory = new CountingSessionFactory
        {
            ThrowOnSerialize = corrupt,
            ThrowOnDeserialize = corrupt,
        };

        var threw = false;
        try
        {
            var session = await store.GetOrCreateAsync("s-corrupt", factory);
            await store.SaveAsync("s-corrupt", factory, session);
            await store.GetOrCreateAsync("s-corrupt", factory);   // 再次取也不应抛
        }
        catch
        {
            threw = true;
        }

        return new CorruptionObservation(
            factory.CreateCount, factory.SerializeCount, factory.DeserializeCount,
            SessionStoreProbe.Ids(store), threw);
    }

    // [F4.6] 两臂同契约：内存版与 SQLite 版行为一致（同一套用例两套实现都过）
    // 负控（内存臂）：改坏 SessionStore 任一条（去掉快路径命中、SaveAsync 不再写回）
    //       → 两次观测的 SecondGetHit / PostSaveHit / CreateCount 具体值红。
    // 负控（落库臂·结构断言）：把 `class SqliteSessionStore : ISessionStore` 改成不实现该接口、
    //       去掉 factory.SerializeAsync / UpsertAsync / factory.DeserializeAsync → 对应 Contains 红；
    //       把 UPSERT 挪到 SerializeAsync 之前 → 顺序断言红。
    // 【退化】未运行落库臂（原因见文件头）：两次「臂」实为两个独立内存版实例，逐字段比对具体值；
    //       落库臂只验「实现同一契约且走同一 ISessionFactory」的结构要素。
    [Fact]
    public async Task F46_TwoArms_SameContract()
    {
        // ① 同一套断言跑内存版两次（两个独立实例）：逐字段相等
        var armA = await RunContractSuite();
        var armB = await RunContractSuite();
        Assert.Equal(armA.CreateCount, armB.CreateCount);
        Assert.Equal(armA.SerializeCount, armB.SerializeCount);
        Assert.Equal(armA.CachedIds, armB.CachedIds);
        Assert.Equal(armA.SecondGetHit, armB.SecondGetHit);
        Assert.Equal(armA.PostSaveHit, armB.PostSaveHit);
        Assert.Equal(armA.SaveThrew, armB.SaveThrew);

        // 具体值：同 id 命中、写回后仍命中、只新建 1 次、内存版不序列化、存储 1 条
        Assert.Equal(1, armA.CreateCount);
        Assert.Equal(0, armA.SerializeCount);
        Assert.Equal(new[] { "s-contract" }, armA.CachedIds);
        Assert.True(armA.SecondGetHit);
        Assert.True(armA.PostSaveHit);
        Assert.False(armA.SaveThrew);

        // 反例对照：不同 id 必须新建，证明上面的「命中」不是恒真断言
        var mismatchStore = new SessionStore(
            Options.Create(new MemoryOptions { SessionTtlMinutes = 0, MaxCachedSessions = 0 }),
            TimeProvider.System);
        var mismatchFactory = new CountingSessionFactory();
        var m1 = await mismatchStore.GetOrCreateAsync("a", mismatchFactory);
        var m2 = await mismatchStore.GetOrCreateAsync("b", mismatchFactory);
        Assert.NotSame(m1, m2);
        Assert.Equal(2, mismatchFactory.CreateCount);

        // ② 落库臂结构断言：同一接口、同一 options/time、同一 ISessionFactory 的序列化 / 反序列化
        var source = ReadSource("Memory", "AgentWorkbench.Memory.Persistence", "SqliteSessionStore.cs");
        Assert.Contains("class SqliteSessionStore : ISessionStore", source);
        Assert.Contains("ISessionStateRepository _repository", source);
        Assert.Contains("IOptions<MemoryOptions> options", source);
        Assert.Contains("TimeProvider timeProvider", source);
        Assert.Contains("IsExpired(record.ExpiresAt)", source);
        Assert.Contains("factory.DeserializeAsync(document.RootElement.Clone()", source);
        Assert.Contains("factory.SerializeAsync(session, cancellationToken)", source);
        Assert.Contains("StateJson = state.GetRawText()", source);
        Assert.Contains("UpsertAsync(new SessionStateRecord", source);
        Assert.Contains("SessionTtlMinutes", source);

        // 写回顺序：先序列化，再 UPSERT（F4.4 的落库臂语义）
        var serializeAt = source.IndexOf("factory.SerializeAsync", StringComparison.Ordinal);
        var upsertAt = source.IndexOf("_repository.UpsertAsync", StringComparison.Ordinal);
        Assert.True(serializeAt >= 0 && upsertAt > serializeAt,
            "落库版写回必须先 factory.SerializeAsync 再 _repository.UpsertAsync");
    }

    private sealed record ContractObservation(
        int CreateCount, int SerializeCount, string[] CachedIds,
        bool SecondGetHit, bool PostSaveHit, bool SaveThrew);

    /// <summary>与实现无关的 ISessionStore 契约用例：get-or-create 命中 + 写回后仍命中。</summary>
    private static async Task<ContractObservation> RunContractSuite()
    {
        var store = new SessionStore(
            Options.Create(new MemoryOptions { SessionTtlMinutes = 0, MaxCachedSessions = 0 }),
            TimeProvider.System);
        var factory = new CountingSessionFactory();

        var first = await store.GetOrCreateAsync("s-contract", factory);
        var second = await store.GetOrCreateAsync("s-contract", factory);
        var secondGetHit = ReferenceEquals(first, second);

        var saveThrew = false;
        try
        {
            await store.SaveAsync("s-contract", factory, second);
        }
        catch
        {
            saveThrew = true;
        }

        var third = await store.GetOrCreateAsync("s-contract", factory);
        return new ContractObservation(
            factory.CreateCount, factory.SerializeCount,
            SessionStoreProbe.Ids(store), secondGetHit,
            ReferenceEquals(first, third), saveThrew);
    }

    // [F4.7] 长期记忆应与 ADR 一致（停用不删）：LongTermEnabled 默认必须为 false
    // 负控：把 MemoryOptions.cs:23 的 `= true` 改成 `= false`（正确实现）→ 本例转绿。
    // 预期红：红是正确信号。当前默认值 true 与 ADR 0016 §五 A5 / B7「长期记忆停用不删」相反，
    //       不要改生产代码让它绿。
    [Fact]
    public void F47_DefaultOptions_LongTermEnabledMustBeFalse()
    {
        // 反例对照（绿）：同一个对象里 SessionEnabled 默认 true，证明本例读取默认值的方式有效，
        // 不是「什么默认值都断言 false」的恒红断言。
        Assert.True(new MemoryOptions().SessionEnabled);

        // (1) 读配置默认值：直接 new
        Assert.False(new MemoryOptions().LongTermEnabled,
            "ADR 0016 §五 A5/B7：长期记忆默认停用；当前默认 true，此断言预期红");

        // (2) 读配置默认值：经 IOptions 包装（与生产注入路径一致）
        var viaOptions = Options.Create(new MemoryOptions()).Value;
        Assert.False(viaOptions.LongTermEnabled,
            "Options.Create 暴露的默认值也必须为 false；当前为 true，此断言预期红");
    }

    // [F4.7] 默认值在源码层面也必须为 false（验收方式「读配置默认值」）
    // 负控：把 MemoryOptions.cs:23 的 `= true` 改成 `= false` → 本例转绿。
    // 预期红：红是正确信号。拆成独立 [Fact]，避免被上一条预期红的断言抛异常中断后根本不执行。
    [Fact]
    public void F47_OptionsSource_DefaultMustBeFalse()
    {
        var source = ReadSource("Runtime", "AgentWorkbench.Runtime.Session", "MemoryOptions.cs");
        Assert.Contains("LongTermEnabled { get; set; } = false;", source);   // 当前 = true → 红
    }

    // [F4.7] 工具注册表的 fallback 也不得默认放行（验收方式「读配置默认值 + 工具注册表」）
    // 负控：把 MemoryPersistenceServiceCollectionExtensions.cs:38 的 `?? true` 改成 `?? false` → 本例转绿。
    // 预期红：红是正确信号。源码在 Memory.Persistence，测试项目未引用该程序集 → 退化为源码结构断言。
    [Fact]
    public void F47_ToolRegistration_FallbackMustBeFalse()
    {
        var source = ReadSource(
            "Memory", "AgentWorkbench.Memory.Persistence", "MemoryPersistenceServiceCollectionExtensions.cs");

        // 绿断言：确认定位到的确实是「按开关注册 remember/recall 工具」的那段代码
        Assert.Contains("if (LongTermEnabled(configuration))", source);
        Assert.Contains("services.AddSingleton<IAgentToolProvider, RememberTool>()", source);
        Assert.Contains("services.AddSingleton<IAgentToolProvider, RecallTool>()", source);

        // 红断言：fallback 默认必须停用
        Assert.Contains("?? false", source);   // 当前 ?? true → 红
    }
}
