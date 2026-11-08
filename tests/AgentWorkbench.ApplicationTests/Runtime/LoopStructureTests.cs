using System.Reflection;
using AgentWorkbench.Shared;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// F1.5 终态可归因 + F1.7 不自研循环。结构断言，不依赖模型运行。
/// </summary>
public sealed class LoopStructureTests
{
    [Fact]
    public void 终态与归类码_两两不重复且关键值齐全()
    {
        var statuses = Literals(typeof(AgentRunStatus));
        var codes = Literals(typeof(AgentRunCode));

        Assert.Equal(statuses.Count, statuses.Distinct().Count());
        Assert.Equal(codes.Count, codes.Distinct().Count());

        foreach (var expected in new[]
        {
            "succeeded", "failed", "timeout", "cancelled",
            "iteration_limit", "budget_exceeded", "loop_detected", "refused"
        })
            Assert.Contains(expected, statuses);

        foreach (var expected in new[]
        {
            "run_timeout", "model_timeout", "iteration_limit", "loop_detected",
            "budget_exceeded", "cancelled", "tool_error", "unhandled_error"
        })
            Assert.Contains(expected, codes);

        // 负控 [F1.5]：把任意两个 code 常量写成同一字符串 → 本条必须红。
    }

    [Fact]
    public void 状态串不超过列宽16()
    {
        var statuses = Literals(typeof(AgentRunStatus));

        Assert.NotEmpty(statuses);
        foreach (var value in statuses)
            Assert.True(value.Length <= 16, $"{value} 超过 agent_runs.status 列宽 16");
        // 负控 [F1.5]：新增一个超长状态值 → 本条必须红。
    }

    [Fact]
    public void Harness_不得二次包裹函数调用环()
    {
        var source = ReadSource(
            "src/Core/Runtime/AgentWorkbench.Runtime.Harness/AgentHarnessService.cs");

        Assert.Contains("UseProvidedChatClientAsIs", source);

        // 注释里解释「为什么不用 AIContextProviders」是合法的，只扫代码行。
        var codeLines = source.Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("//") && !line.StartsWith("*") && !line.StartsWith("/*"))
            .ToList();
        Assert.NotEmpty(codeLines); // 扫描器自证：扫不到代码行时不得恒绿
        Assert.DoesNotContain(codeLines, line => line.Contains("AIContextProviders"));
        // 负控 [F1.7]：给 ChatClientAgentOptions 挂 AIContextProviders → 本条必须红。
    }

    private static List<string> Literals(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string) && field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

    private static string ReadSource(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentWorkbench.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relativePath));
    }
}
