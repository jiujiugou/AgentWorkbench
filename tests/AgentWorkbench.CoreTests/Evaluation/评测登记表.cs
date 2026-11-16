using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AgentWorkbench.Evaluation.Kernel;
using AgentWorkbench.Observability;
using Xunit;

namespace AgentWorkbench.CoreTests.Evaluation;

/// <summary>
/// `evaluation.toml` 与代码的互证。<b>这是「清单不漂移」的唯一保证。</b>
///
/// 背景：评测清单以前写在 md 里，改代码不改文档就成了「文档说 7 条、代码 13 条」——
/// 读文档的人据此判断覆盖面，结论直接错。清单搬到 toml 还不够，必须有东西读它，
/// 否则它和 md 没有区别（`agent-parts.toml` 头部那句话：「别把已经写成 toml 当成已经约束住」）。
///
/// <b>六条断言，双向互证：</b>
/// ① 表里 status = wired 的每个 <c>api</c>，必须是 <see cref="KernelChecks"/> 的公开静态判据方法；
/// ② <see cref="KernelChecks"/> 的每个公开静态判据方法，必须在表里有一条 <c>api</c>；
/// ③ 每条 <c>evidence</c> 的测试方法必须真实存在；
/// ④ 每条 <c>covers</c> 点名的字段必须是 <see cref="ExecutionTrace"/> 上真实存在的属性；
/// ⑤ 内核量必须处于 wired（改成 planned 会让前四条静默跳过）；目标量不得有非空 api；
/// ⑥ 登记在案的 aggregate / sample 类型必须存在，且聚合有公开静态入口。
///
/// <b>为什么不引 toml 解析器</b>：为了一个两百行的登记表引一个依赖，成本高于收益，
/// 而这里只需要「按行提几个键」。反过来，解析得越宽松，越不容易因为格式变化误报。
///
/// <b>层 × 方向</b>：只读文件与反射，不触达运行时。
/// </summary>
public sealed class 评测登记表
{
    /// <summary>负控：把 KernelChecks 任一判据改名而不改 toml → ①红；只加方法不登记 → ②红。</summary>
    [Fact]
    public void 表里的每条判据与代码双向对齐()
    {
        var kernelSection = KernelSection();
        var declared = Matches(kernelSection, "api\\s*=\\s*\"([A-Za-z]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var implemented = typeof(KernelChecks)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            // EvalCheck 是框架的委托类型，按返回类型名判断即可；引它的命名空间只为这一处不值得。
            .Where(m => m.ReturnType.Name == "EvalCheck")
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        // ① 表里有、代码没有 —— 多半是改名后忘了同步，或者写了没实现。
        var ghost = declared.Except(implemented).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(ghost.Length == 0,
            $"evaluation.toml 登记了不存在的判据：{string.Join(", ", ghost)}");

        // ② 代码里有、表里没有 —— 新加判据却没登记，清单立刻开始漂移。
        var unlisted = implemented.Except(declared).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(unlisted.Length == 0,
            $"KernelChecks 有未登记的判据：{string.Join(", ", unlisted)}");
    }

    /// <summary>负控：把某条 evidence 改成不存在的测试名 → 红。</summary>
    [Fact]
    public void 每条判据的佐证测试都真实存在()
    {
        var kernelSection = KernelSection();
        var names = Matches(kernelSection, """evidence\s*=\s*\[([^\]]*)\]""")
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(names);

        var existing = typeof(评测登记表).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = names.Where(n => !existing.Contains(n)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0,
            $"evaluation.toml 的 evidence 指向不存在的测试：{string.Join(", ", missing)}");
    }

    /// <summary>负控：把 covers 改成 Trace 上不存在的字段（如 <c>Steps.Truncate</c>）→ 红。</summary>
    [Fact]
    public void 每条判据点名的字段都存在于轨迹上()
    {
        var kernelSection = KernelSection();
        var paths = Matches(kernelSection, """covers\s*=\s*\[([^\]]*)\]""")
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(paths);
        foreach (var path in paths)
            AssertFieldExists(path);
    }

    /// <summary>负控：把 kernel 的 status 改成 planned → 红（这会让上面三条静默跳过）。</summary>
    [Fact]
    public void 内核量必须处于已接线状态()
    {
        var kernelSection = KernelSection();
        var header = kernelSection[..kernelSection.IndexOf("[[measure.kernel.check]]", StringComparison.Ordinal)];
        Assert.Contains("status = \"wired\"", header, StringComparison.Ordinal);
    }

    /// <summary>goal 那节还没建：它的判据没有 api / evidence，不能混进内核的核对里。</summary>
    [Fact]
    public void 目标量仍是计划态时不得登记判据实现()
    {
        var text = File.ReadAllText(TomlPath());
        var goalStart = text.IndexOf("[measure.goal]", StringComparison.Ordinal);
        Assert.True(goalStart > 0, "evaluation.toml 缺少 [measure.goal] 一节");

        var goalSection = text[goalStart..];
        Assert.Contains("status = \"planned\"", goalSection, StringComparison.Ordinal);
        // 允许写 api = ""（占位），但不许出现非空实现名——那会让「计划态」变成「已实现」的假象。
        Assert.DoesNotMatch("api\\s*=\\s*\"[A-Za-z]+\"", goalSection);
    }

    /// <summary>负控：把 aggregate 改成不存在的类型名 → 红。</summary>
    [Fact]
    public void 登记在案的聚合类型真实存在且有入口()
    {
        var kernelSection = KernelSection();

        var aggregate = Regex.Match(kernelSection, "aggregate\\s*=\\s*\"([A-Za-z]+)\"").Groups[1].Value;
        var sample = Regex.Match(kernelSection, "sample\\s*=\\s*\"([A-Za-z]+)\"").Groups[1].Value;

        Assert.False(string.IsNullOrEmpty(aggregate), "[measure.kernel] 必须登记 aggregate");
        Assert.False(string.IsNullOrEmpty(sample), "[measure.kernel] 必须登记 sample");

        var assembly = typeof(KernelChecks).Assembly;
        var report = assembly.GetType($"AgentWorkbench.Evaluation.Kernel.{aggregate}");
        var sampleType = assembly.GetType($"AgentWorkbench.Evaluation.Kernel.{sample}");

        Assert.True(report is not null, $"登记的聚合类型不存在：{aggregate}");
        Assert.True(sampleType is not null, $"登记的采样类型不存在：{sample}");

        // 聚合必须有公开静态入口，否则「样本怎么喂进去」只能靠读实现。
        var entry = report!.GetMethod("From", BindingFlags.Public | BindingFlags.Static);
        Assert.True(entry is not null, $"{aggregate} 缺少公开静态 From(...) 入口");
    }

    // ── 读取 ────────────────────────────────────────────────────────

    private static string TomlPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "evaluation.toml");
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "找不到 evaluation.toml：测试运行目录不在仓库内，或清单被改名了。");
    }

    /// <summary>切出 kernel 那一段：从 <c>[measure.kernel]</c> 到 <c>[measure.goal]</c> 之前。</summary>
    private static string KernelSection()
    {
        var text = File.ReadAllText(TomlPath());
        var start = text.IndexOf("[measure.kernel]", StringComparison.Ordinal);
        Assert.True(start > 0, "evaluation.toml 缺少 [measure.kernel] 一节");

        var end = text.IndexOf("[measure.goal]", StringComparison.Ordinal);
        Assert.True(end > start, "[measure.goal] 必须排在 [measure.kernel] 之后");

        return text[start..end];
    }

    private static IEnumerable<Match> Matches(string text, string pattern)
        => Regex.Matches(text, pattern).Cast<Match>();

    /// <summary>
    /// 字段路径必须是轨迹上真实存在的属性，含 <c>Steps.X</c> 这种成员路径。
    /// 集合类型自动解包到元素类型——否则 <c>Steps.Truncated</c> 会被判成不存在。
    /// </summary>
    private static void AssertFieldExists(string path)
    {
        var type = typeof(ExecutionTrace);
        foreach (var segment in path.Split('.'))
        {
            var property = type.GetProperty(segment);
            Assert.True(property is not null, $"evaluation.toml 的 covers 指向不存在的字段：{path}（在 {type.Name} 上没有 {segment}）");
            type = property!.PropertyType;

            if (type.IsGenericType)
                type = type.GetGenericArguments()[0];
        }
    }
}
