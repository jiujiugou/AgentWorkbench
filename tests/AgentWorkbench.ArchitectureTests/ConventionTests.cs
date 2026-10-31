using ArchUnitNET.Domain;
using ArchUnitNET.xUnit;
using AgentWorkbench.Persistence;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace AgentWorkbench.ArchitectureTests;

/// <summary>
/// 代码形状约定：把「写在文档里没人读」的规矩变成会红的架构测试。
///
/// 与 <see cref="LayeringTests"/>（依赖方向）和 <see cref="ArchitectureBudgets"/>（规模）并列，
/// 管的是「某个类不许用某样东西」「某个方法必须长什么样」。
/// 依据见 <c>docs/测试标准/测试分层职责.md</c>：架构层 = 不跑行为，靠反射与依赖分析判「架构是否倒退」。
/// </summary>
/// <remarks>
/// <b>基线口径是「违规类型数」，不是「出现次数」</b>——ArchUnitNET 判的是类型依赖，
/// 一个类里写 10 次 <c>Stopwatch</c> 算 1 个违规类型。要求立刻归零会让测试长期红着，红久了没人看，
/// 所以先冻结现状、只许减少（与 <see cref="ArchitectureBudgets"/> 的棘轮同一思路）。
/// </remarks>
public class ConventionTests
{
    private static readonly Architecture Arch = TestArchitecture.Architecture;

    private const string ControllerNamespace = "^AgentWorkbench\\.Webapi\\.Controllers";

    private const string Stopwatch = "System.Diagnostics.Stopwatch";
    private const string PeriodicTimer = "System.Threading.PeriodicTimer";
    private const string BackgroundService = "Microsoft.Extensions.Hosting.BackgroundService";
    private const string Transaction = "System.Data.IDbTransaction";

    private static IReadOnlyList<IType> ControllerTypes =>
        Types().That().ResideInNamespaceMatching(ControllerNamespace).GetObjects(Arch).ToList();

    /// <summary>
    /// C7：请求耗时由 <c>UseSerilogRequestLogging</c> 统一记，Controller 不许自己计时。
    /// 用类型依赖判，所以注释里出现 "Stopwatch" 不算违规——这是它比文本扫描强的地方。
    /// </summary>
    [Fact]
    public void C7_控制器不得自己计时()
    {
        Assert.NotEmpty(ControllerTypes); // 自证：扫不到类型时不许恒绿

        var offenders = ControllerTypes
            .Where(t => t.Dependencies.Any(d => d.Target.FullName == Stopwatch))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.True(offenders.Count <= 3,
            $"C7：{offenders.Count} 个 Controller 依赖 {Stopwatch}，基线 3。"
            + $"请求耗时已由 UseSerilogRequestLogging 记录，业务代码再记一遍只是噪音。"
            + $"当前违规：{string.Join("、", offenders)}");
    }

    /// <summary>
    /// C4a：长驻工作只能由宿主启动。
    /// 判据不是「用没用 Task」，而是这个执行单元会不会活过当前请求——
    /// 自己起的线程在进程退出时被硬杀（数据半写）、异常被吞（unobserved）、测试里等不到它结束。
    /// </summary>
    [Fact]
    public void C4a_长驻工作必须由宿主启动()
    {
        var hosted = Arch.Types
            .OfType<ArchUnitNET.Domain.Class>()
            .Where(t => t.BaseClass?.FullName == BackgroundService)
            .Select(t => t.FullName)
            .ToHashSet();

        Assert.True(hosted.Count >= 3,
            $"只认出 {hosted.Count} 个 BackgroundService 子类，预期至少 3 个"
            + "（DocumentParseWorker / MemoryRetentionService / WorkflowWorker）——识别失效，这条不算数。");

        var markers = new[] { PeriodicTimer, "System.Threading.Thread", "System.Threading.Timer" };

        var offenders = Arch.Types
            .Where(t => t.Namespace.FullName.StartsWith("AgentWorkbench", StringComparison.Ordinal))
            .Where(t => !hosted.Contains(t.FullName))
            .Where(t => t.Dependencies.Any(d => markers.Contains(d.Target.FullName)))
            .Select(t => $"{t.Name}（{t.Dependencies.First(d => markers.Contains(d.Target.FullName)).Target.Name}）")
            .OrderBy(x => x)
            .ToList();

        Assert.True(offenders.Count == 0,
            "C4a：以下类型在非 BackgroundService 里起了长驻工作，进程退出时会被硬杀、异常会被吞：\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// §8.3：仓储写方法必须接受调用方传入的事务。
    /// 存量 5 个写方法不追溯（基线 5），<b>新增的第六个必须带 <c>IDbTransaction</c> 参数</b>——
    /// 否则每次写各自开事务，跨表写入就无法整体回滚。
    /// </summary>
    [Fact]
    public void S8_3_仓储写方法必须接受事务参数()
    {
        var writers = MethodMembers().That()
            .AreDeclaredIn(typeof(DocumentRepository))
            .And().HaveNameMatching("(Insert|Update|Delete|Replace).*Async")
            .GetObjects(Arch)
            .ToList();

        Assert.NotEmpty(writers); // 自证：一个都没选中说明规则写错了

        var without = writers
            .Where(m => !m.Parameters.Any(p => p.FullName == Transaction))
            .Select(m => $"{m.Name}({string.Join(", ", m.Parameters.Select(p => p.Name))})")
            .OrderBy(n => n)
            .ToList();

        Assert.True(without.Count <= 5,
            $"§8.3：{without.Count} 个仓储写方法没有 {Transaction} 参数，基线 5。"
            + $"新增的写方法必须接受调用方传入的事务（理由见实现设计 §4）。当前违规：{string.Join("、", without)}");
    }
}
