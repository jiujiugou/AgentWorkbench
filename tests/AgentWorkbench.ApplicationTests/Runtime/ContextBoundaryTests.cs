using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Runtime;

/// <summary>
/// F3.7 / F3.8：结构断言。直接 File.ReadAllText 扫描源码，不依赖被扫类型的可访问性。
/// 仓库根通过向上查找 AgentWorkbench.slnx 定位，避免硬编码相对层数。
/// </summary>
public sealed class ContextBoundaryTests
{
    private const string ContextDir = "src/Core/Runtime/AgentWorkbench.Runtime.Context";
    private const string HarnessDir = "src/Core/Runtime/AgentWorkbench.Runtime.Harness";

    /// <summary>
    /// AC-02 业务禁词，取自 Py 仓 ADR 0017 §六：扫类型名 / 字段名 / 方法名，
    /// 注释与说明文字里提到不算违规（所以下面用 StripComments 只扫代码行）。
    /// </summary>
    /// <remarks>
    /// `ActionSpec` / `ActionRequest` / `ActionResult` / `SideEffecting` **不是**业务禁词：
    /// 它们是底座端口契约的抽象类型（ADR 0017 §三），业务无关，出现是合法的。
    /// 把端口词列进禁词表会让换域演练（AC-01）误判。
    /// </remarks>
    private static readonly string[] BusinessForbiddenWords =
    {
        "PurchaseOrder", "Supplier", "AmountLimit", "TaxCode", "SAP",
        "用友", "金蝶", "Modbus", "ERPNext",
        "采购单", "供应商", "税码", "物料编码", "金额阈值", "成本口径",
        "月结期间", "审批层级", "单据状态码",
    };

    /// <summary>从测试输出目录 / 当前目录逐级向上找 AgentWorkbench.slnx，返回其所在目录。</summary>
    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgentWorkbench.slnx")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("未找到 AgentWorkbench.slnx，无法定位仓库根。");
    }

    /// <summary>读取若干源码目录下（仅顶层）的全部 .cs 文本。</summary>
    private static IReadOnlyList<string> ReadSourceFiles(string repoRoot, params string[] relativeDirs)
        => relativeDirs
            .SelectMany(d => Directory.GetFiles(
                Path.Combine(repoRoot, d), "*.cs", SearchOption.TopDirectoryOnly))
            .Select(File.ReadAllText)
            .ToList();

    /// <summary>
    /// 去掉纯注释行（//、///、/*、* 开头），只留代码行。
    /// 源码里大量出现「解释为何不用 AIContextProviders」的注释，那是文档不是「挂了 provider」，
    /// 必须跳过，否则结构断言会把「说明不要挂」误判成「挂了」。
    /// </summary>
    private static string StripComments(string source)
    {
        var code = new List<string>();
        foreach (var line in source.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("//", StringComparison.Ordinal) ||
                t.StartsWith("/*", StringComparison.Ordinal) ||
                t.StartsWith("*", StringComparison.Ordinal))
                continue;
            code.Add(line);
        }
        return string.Join("\n", code);
    }

    /// <summary>精确计数子串出现次数（Ordinal，大小写敏感）。</summary>
    private static int CountOccurrences(string text, string word)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += word.Length;
        }
        return count;
    }

    // [F3.7] 边界：Runtime.Context 不读库、不检索、无业务词（AC-02 禁词零命中）
    // 负控：在 Runtime.Context 的任一 .cs 代码行里引入 PurchaseOrder / 供应商 等
    //       → 对应禁词的 Assert.Equal(0, ...) 会红。
    [Fact]
    public void F37_ContextModule_ContainsNoBusinessWords()
    {
        var root = FindRepoRoot();
        var sources = ReadSourceFiles(root, ContextDir);

        // 必须真的扫到了源码，防止「扫空集合」造成的恒真断言
        Assert.True(sources.Count >= 5, $"应至少读到 5 个 .cs 源文件，实际 {sources.Count}");
        var code = StripComments(string.Join("\n", sources));
        Assert.NotEmpty(code);

        foreach (var word in BusinessForbiddenWords)
            Assert.Equal(0, CountOccurrences(code, word));   // 每个业务词命中次数精确为 0

        // 边界补充：不读库、不检索 —— 代码里不出现这些基础设施/检索调用符号
        foreach (var symbol in new[] { "DbConnection", "SqlConnection", "DbContext", "IRepository", "Embedding" })
            Assert.Equal(0, CountOccurrences(code, symbol));

        // 反例对照：同一套扫描器用在含业务词的样本上必须命中 > 0
        // （负控：若扫描器恒返回 0，这条会红 —— 证明上面的「零命中」不是恒真断言）
        const string sample = "public sealed record PurchaseOrder(string Supplier, decimal AmountLimit);";
        Assert.True(CountOccurrences(sample, "PurchaseOrder") > 0);
        Assert.True(CountOccurrences(sample, "Supplier") > 0);
        Assert.True(CountOccurrences(sample, "AmountLimit") > 0);
    }

    // [F3.8] 注入的三条线（2026-11-14 Context 重构后重写；旧版钉的是 `IChatClient` 装饰器 + `_context.Wrap`，
    // 那两样已被删除——指令形态与历史都有框架等价物，按「绝对不造轮子」不再自写装饰器）。
    // 1) 不挂 AIContextProviders：它会绕过工具环守卫（设计使然，不是 bug）；
    // 2) 不自写 ChatClient 装饰器：`ContextInjectingChatClient` 不该长回来；
    // 3) 指令赋值只有一处：两处赋值会让「run 指令」和「每轮指令」互相覆盖。
    // 负控：加回 provider / 重新引入装饰器 / 多写一处 `Instructions =` → 本条红。
    [Fact]
    public void F38_指令走ChatOptions_不挂AIContextProviders_不自写装饰器()
    {
        var root = FindRepoRoot();
        var sources = ReadSourceFiles(root, ContextDir, HarnessDir);

        Assert.True(sources.Count >= 8, $"应至少读到 8 个 .cs 源文件，实际 {sources.Count}");

        // 只扫代码行：AIContextProviders 在当前源码里只出现在「解释为何不用它」的注释里。
        var code = string.Join("\n", sources.Select(StripComments));
        Assert.Equal(0, CountOccurrences(code, "AIContextProviders"));

        // 装饰器与 Wrap 都已删除：它们出现在代码行里就说明有人把旧形态抄回来了。
        Assert.Equal(0, CountOccurrences(code, "ContextInjectingChatClient"));
        Assert.Equal(0, CountOccurrences(code, "_context.Wrap"));

        // 单一注入点：ChatOptions.Instructions 的赋值只允许出现一次。
        // 只数「行首就是 Instructions =」的语句——record 构造参数里的 `Instructions = null`
        // 是默认值，不是注入点，混进来会让这条断言数错。
        var assignments = code.Split('\n')
            .Select(l => l.Trim())
            .Count(l => l.StartsWith("Instructions =", StringComparison.Ordinal));
        Assert.Equal(1, assignments);

        // 反例对照：同一套扫描器能抓到「改挂 provider」的代码
        // （负控：若 StripComments / 扫描器恒返回 0，这条会红 —— 证明零命中不是恒真）
        var broken = "var options = new ChatClientAgentOptions\n{\n    AIContextProviders = [new MyProvider()],\n};";
        Assert.True(CountOccurrences(StripComments(broken), "AIContextProviders") > 0);
    }
}
