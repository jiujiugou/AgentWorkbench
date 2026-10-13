using System.Reflection;
using System.Text.RegularExpressions;
using AgentWorkbench.AgentHarness;
using Xunit;

namespace AgentWorkbench.ArchitectureTests;

/// <summary>
/// 复杂度预算：把「只减不增」变成 CI 门禁。
/// 预算值按 2026-10-13 实测现状冻结；每完成一轮瘦身，把对应预算收紧到新现状，再降一点。
/// 现有 LayeringTests / ModuleCycleTests 管「依赖方向」，本类管「规模」——两者合起来才是完整的架构约束。
/// </summary>
/// <remarks>
///  redness 验证方式（改完预算测试后自证一次不是永远绿）：临时把任一预算值改成低于现状（如 MaxSrcProjects = 19），
/// 对应用例必须变红，再改回来。做不到红的就是假门禁。
/// </remarks>
public class ArchitectureBudgets
{
    private static readonly string RepoRoot = FindRepoRoot();

    // ── 预算值（2026-10-13 首轮冻结 20 / 300 / 15 / 15；当日拆 UseKnowledge 暗路后 Harness 依赖 15 → 13，随即收紧）──
    // 阶段 4 砍伐：删 Context（20→19）、并 Safety 进 ToolExecution（19→18）、并 Knowledge 注册进 Knowledge.Local（18→17）。
    //              Program 注册 15→13；新增 src 总行数棘轮（砍伐成果只减不增）。
    private const int MaxSrcProjects = 17;
    private const int MaxSrcLoc = 10000;
    private const int MaxAgentLoopLoc = 300;
    private const int MaxHarnessCtorParams = 13;
    private const int MaxProgramRegistrations = 13;

    /// <summary>轨迹回放入口的源码标记：评测必须有一条用例消费它（阶段 3：轨迹不得退回「只写不读」）。</summary>
    private const string ReplayConsumptionMarker = "GetRunAsync(";

    /// <summary>永远不该出现的标记：用于负控自证「扫描不是恒真」。</summary>
    private const string ImpossibleMarker = "GetRunAsyncThisMarkerNeverAppearsOnEarth(";

    [Fact]
    public void Src_project_count_should_not_exceed_budget()
    {
        var projects = Directory.GetDirectories(Path.Combine(RepoRoot, "src"))
            .Count(dir => Directory.GetFiles(dir, "*.csproj").Length > 0);

        Assert.True(
            projects <= MaxSrcProjects,
            $"src 项目数 {projects} 超预算 {MaxSrcProjects}。新能力应以「工具」进入 AgentLoop 已有的函数调用环，不准新开 src 项目；");
    }

    [Fact]
    public void Src_total_loc_should_not_exceed_budget()
    {
        var srcRoot = Path.Combine(RepoRoot, "src");
        var loc = Directory.GetDirectories(srcRoot).Sum(CountLoc);

        Assert.True(
            loc <= MaxSrcLoc,
            $"src 总行数 {loc} 超预算 {MaxSrcLoc}（阶段 4 砍伐后的棘轮）。"
            + "想加代码又不想超：先把等量的东西删掉，或把能力做成工具而不是新模块；");
    }

    [Fact]
    public void AgentLoop_loc_should_not_exceed_budget()
    {
        var loc = CountLoc(Path.Combine(RepoRoot, "src", "AgentWorkbench.AgentLoop"));

        Assert.True(
            loc <= MaxAgentLoopLoc,
            $"AgentLoop {loc} 行超预算 {MaxAgentLoopLoc}。核心循环必须保持能一口气读完；新逻辑放工具或独立步骤，别堆进 loop；");
    }

    [Fact]
    public void Harness_constructor_dependencies_should_not_exceed_budget()
    {
        var deps = typeof(AgentHarnessService)
            .GetConstructors()
            .Max(ctor => ctor.GetParameters().Length);

        Assert.True(
            deps <= MaxHarnessCtorParams,
            $"AgentHarnessService 构造依赖 {deps} 个超预算 {MaxHarnessCtorParams}。harness 只做装配+收尾+归类，"
            + "单项能力应下沉为工具或独立服务；");
    }

    [Fact]
    public void Program_registration_lines_should_not_exceed_budget()
    {
        var programCs = File.ReadAllText(Path.Combine(RepoRoot, "src", "AgentWorkbench.Webapi", "Program.cs"));
        var registrations = Regex.Count(programCs, @"\.AddNitro[A-Za-z]+\s*\(");

        Assert.True(
            registrations <= MaxProgramRegistrations,
            $"Program.cs 注册调用 {registrations} 处超预算 {MaxProgramRegistrations}。装配根本身必须保持薄；");
    }

    /// <summary>
    /// 阶段 3 收尾的<b>消费断言</b>：轨迹回放入口必须被评测用例消费。
    /// 防的是「回归链被静默移除」——把回放测试删掉后，轨迹又回到「只写不读」的死数据状态，
    /// 而所有既有测试仍然全绿（这正是「验收假绿」的形态）。所以这条断言本身就是那道保险。
    /// </summary>
    [Fact]
    public void Trace_replay_must_be_consumed_by_evaluation_tests()
    {
        var evaluationDir = Path.Combine(RepoRoot, "tests", "AgentWorkbench.EvaluationTests");
        var consumers = CountMarker(evaluationDir, ReplayConsumptionMarker);

        Assert.True(
            consumers > 0,
            $"评测项目里没有任何源码引用回放入口 `{ReplayConsumptionMarker}`——轨迹会退回「只写不读」的死数据。"
            + "若确实删掉了回放用例，请先想清楚「每次 run 的轨迹，还有哪条测试会因它被篡改而变红」；");

        // 判据自身的负控：同一扫描对「绝不可能出现的标记」必须返回 0，证明它不是恒真门禁。
        Assert.Equal(0, CountMarker(evaluationDir, ImpossibleMarker));
    }

    private static int CountLoc(string projectDir)
        => SourceFiles(projectDir).Sum(file => File.ReadAllLines(file).Length);

    /// <summary>统计目录下 .cs 源文件中出现 <paramref name="marker"/> 的次数（排除 obj / bin）。</summary>
    private static int CountMarker(string projectDir, string marker)
        => SourceFiles(projectDir).Sum(file => Regex.Count(File.ReadAllText(file), Regex.Escape(marker)));

    private static IEnumerable<string> SourceFiles(string projectDir)
        => Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
            .Where(file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentWorkbench.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, $"未能从 {AppContext.BaseDirectory} 向上找到 AgentWorkbench.slnx（仓库根）。");
        return dir!.FullName;
    }
}
