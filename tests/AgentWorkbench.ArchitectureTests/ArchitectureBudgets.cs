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
    private const int MaxSrcProjects = 20;
    private const int MaxAgentLoopLoc = 300;
    private const int MaxHarnessCtorParams = 13;
    private const int MaxProgramRegistrations = 15;

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

    private static int CountLoc(string projectDir)
        => SourceFiles(projectDir).Sum(file => File.ReadAllLines(file).Length);

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
