using System.Text.RegularExpressions;
using Xunit;

namespace AgentWorkbench.ArchitectureTests;

/// <summary>
/// 模块引用门禁：只读每个 csproj 的 <c>ProjectReference</c>，判断引用本身有没有错。
/// 范围是<b>引用</b>——路径存在、不自引、不重复、不成环、方向不越界；
/// 不读源码内容、不读 TOML、不读文档、不管 slnx。
/// </summary>
/// <remarks>
/// 检查逻辑是纯函数 <see cref="Validate"/>，输入只有「项目路径 → 引用路径」这张图，
/// 所以 R0 自证可以用合成图，不必往工作区写探针文件。
/// </remarks>
public sealed class 模块引用门禁
{
    // ── 正例 ──────────────────────────────────────────────────────────

    [Fact]
    public void 当前所有项目的模块引用都合法()
    {
        var graph = BuildGraph(RepoRoot());
        Assert.Empty(Validate(graph));
    }

    // 防恒绿：BuildGraph 若扫不到项目，Validate 会「什么问题都没有」也绿。
    [Fact]
    public void 项目图确实被解析到_不是空集合恒绿()
    {
        var graph = BuildGraph(RepoRoot());
        Assert.True(graph.Count >= 20, $"只解析到 {graph.Count} 个项目");
        Assert.True(graph.Values.Sum(r => r.Count) >= 30, "一条引用都没解析到");
    }

    // ── R0 自证：检查器必须能被证伪，且不是恒红 ───────────────────────

    [Fact]
    public void R0自证_合法图绿_八种引用错误各自都能变红()
    {
        Assert.Empty(Validate(Clean()));

        Assert.NotEmpty(Validate(With("Core 引用了 Domains",
            ("src/Core/Runtime/AgentWorkbench.Runtime.Harness/AgentWorkbench.Runtime.Harness.csproj",
             "src/Domains/Erp/AgentWorkbench.Erp.csproj"))));

        Assert.NotEmpty(Validate(With("引用了不存在的 csproj",
            ("src/Core/Runtime/AgentWorkbench.Runtime.Harness/AgentWorkbench.Runtime.Harness.csproj",
             "src/Core/DoesNotExist/AgentWorkbench.DoesNotExist.csproj"))));

        Assert.NotEmpty(Validate(With("Ports 不再零引用",
            ("src/Core/Ports/AgentWorkbench.Ports.csproj",
             "src/Core/Foundation/AgentWorkbench.Shared/AgentWorkbench.Shared.csproj"))));

        Assert.NotEmpty(Validate(With("Domains 越过 Ports 引用 Runtime",
            ("src/Domains/Erp/AgentWorkbench.Erp.csproj",
             "src/Core/Runtime/AgentWorkbench.Runtime.Harness/AgentWorkbench.Runtime.Harness.csproj"))));

        Assert.NotEmpty(Validate(With("内核引用了存储",
            ("src/Core/Runtime/AgentWorkbench.Runtime.Harness/AgentWorkbench.Runtime.Harness.csproj",
             "src/Implementations/AgentWorkbench.Persistence/AgentWorkbench.Persistence.csproj"))));

        // 新规则单独的自证：目标不是 IsStorage 认识的存储项目（Memory.Persistence 名字不以
        // AgentWorkbench.Persistence 结尾），只能靠「底座闭包」这条抓到。
        Assert.NotEmpty(Validate(With("底座引用了实现层",
            ("src/Core/Runtime/AgentWorkbench.Runtime.Harness/AgentWorkbench.Runtime.Harness.csproj",
             "src/Implementations/AgentWorkbench.Memory.Persistence/AgentWorkbench.Memory.Persistence.csproj"))));

        var self = Clean();
        self["src/Core/Ports/AgentWorkbench.Ports.csproj"] =
            ["src/Core/Ports/AgentWorkbench.Ports.csproj"];
        Assert.NotEmpty(Validate(self));  // 自引

        var cyclic = Clean();
        cyclic["src/Core/Foundation/AgentWorkbench.Shared/AgentWorkbench.Shared.csproj"] =
            ["src/Core/Runtime/AgentWorkbench.Runtime.Harness/AgentWorkbench.Runtime.Harness.csproj"];
        Assert.NotEmpty(Validate(cyclic));  // 成环（Harness → Shared → Harness）
    }

    // ── 检查器（纯函数）────────────────────────────────────────────────

    internal static IReadOnlyList<string> Validate(IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var errors = new List<string>();

        foreach (var (from, references) in graph)
        {
            // ① 引用目标必须真实存在（图内即存在：BuildGraph 收全了 src/ 与 tests/ 下所有 csproj）
            foreach (var to in references)
                if (!graph.ContainsKey(to))
                    errors.Add($"悬空引用：{from} → {to}");

            // ② 不自引、不重复
            if (references.Contains(from, StringComparer.OrdinalIgnoreCase))
                errors.Add($"自引用：{from}");
            foreach (var duplicate in references.GroupBy(r => r, StringComparer.OrdinalIgnoreCase)
                         .Where(g => g.Count() > 1))
                errors.Add($"重复引用：{from} → {duplicate.Key}");

            // ③ 方向铁律
            foreach (var to in references)
            {
                if (IsCore(from) && IsDomains(to))
                    errors.Add($"Core 不得引用 Domains：{from} → {to}");
                if (IsDomains(from) && !IsPorts(to))
                    errors.Add($"Domains 只允许引用 Core/Ports：{from} → {to}");
                if (IsKernel(from) && IsStorage(to))
                    errors.Add($"内核侧不得编译期引用存储：{from} → {to}");
                // 底座的引用闭包不许出底座：比「内核不引存储」更广——实现层（Memory.Persistence /
                // Knowledge.Local / Documents）也不许被底座拖进来，否则「底座能单独交付」就不成立。
                // 不排除 IsStorage 目标：知识契约曾只为拿一个词表引用 Domain，正是这条要挡的形状。
                if (IsBase(from) && !IsBase(to))
                    errors.Add($"底座项目只允许引用底座：{from} → {to}");
            }

            // ④ Ports 零引用
            if (IsPorts(from) && references.Count > 0)
                errors.Add($"Ports 必须零项目引用：{from}");
        }

        // ⑤ 项目图无环
        errors.AddRange(FindCycles(graph));
        return errors;
    }

    private static IReadOnlyList<string> FindCycles(IReadOnlyDictionary<string, IReadOnlyList<string>> graph)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        var cycles = new List<string>();

        void Visit(string node)
        {
            if (state.TryGetValue(node, out var mark))
            {
                if (mark == 1)
                {
                    var start = stack.IndexOf(node);
                    cycles.Add("循环引用：" + string.Join(" → ", stack.Skip(start).Append(node)));
                }
                return;
            }

            state[node] = 1;
            stack.Add(node);
            if (graph.TryGetValue(node, out var references))
                foreach (var to in references)
                    if (graph.ContainsKey(to))
                        Visit(to);
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }

        foreach (var node in graph.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            Visit(node);
        return cycles;
    }

    private static bool IsCore(string path) => path.StartsWith("src/Core/", StringComparison.OrdinalIgnoreCase);
    private static bool IsDomains(string path) => path.StartsWith("src/Domains/", StringComparison.OrdinalIgnoreCase);
    private static bool IsPorts(string path) => path.StartsWith("src/Core/Ports/", StringComparison.OrdinalIgnoreCase);
    private static bool IsKernel(string path)
        => path.StartsWith("src/Core/Runtime/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("src/Core/Engineering/", StringComparison.OrdinalIgnoreCase);
    private static bool IsStorage(string path)
        => path.EndsWith("AgentWorkbench.Persistence.csproj", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith("AgentWorkbench.Domain.csproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 底座项目：只收「机制」与「机制的契约」，按路径白名单判定。
    /// </summary>
    /// <remarks>
    /// 刻意不按「Core 下全部」：实现层已搬出 <c>src/Core</c>（2026-11-14），
    /// 但白名单仍按「性质」而不是「目录」写——将来若有人把某个实现挪回 Core，这条照样会红。
    /// 底座的定义是「换一个动作域不改，且一次 run 必需（或是某项能力的契约）」。
    /// </remarks>
    private static bool IsBase(string path)
        => path.StartsWith("src/Core/Runtime/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("src/Core/Engineering/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("src/Core/Ports/", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("src/Core/Tools/", StringComparison.OrdinalIgnoreCase)
           || path.Equals("src/Core/Foundation/AgentWorkbench.Shared/AgentWorkbench.Shared.csproj",
               StringComparison.OrdinalIgnoreCase)
           || path.Equals("src/Core/Knowledge/AgentWorkbench.Knowledge.Abstractions/AgentWorkbench.Knowledge.Abstractions.csproj",
               StringComparison.OrdinalIgnoreCase);

    // ── 合成图（R0 自证用）────────────────────────────────────────────

    private const string Ports = "src/Core/Ports/AgentWorkbench.Ports.csproj";
    private const string Shared = "src/Core/Foundation/AgentWorkbench.Shared/AgentWorkbench.Shared.csproj";
    private const string Harness = "src/Core/Runtime/AgentWorkbench.Runtime.Harness/AgentWorkbench.Runtime.Harness.csproj";
    private const string Erp = "src/Domains/Erp/AgentWorkbench.Erp.csproj";

    private static Dictionary<string, IReadOnlyList<string>> Clean() => new(StringComparer.OrdinalIgnoreCase)
    {
        [Ports] = [],
        [Shared] = [],
        [Harness] = [Ports, Shared],
        [Erp] = [Ports],
    };

    private static Dictionary<string, IReadOnlyList<string>> With(string name, (string From, string To) extra)
    {
        var graph = Clean();
        graph[extra.From] = [.. graph[extra.From], extra.To];
        // 悬空引用的目标故意不进图；其余变异的目标补进图，确保报的是方向错而不是悬空。
        if (name != "引用了不存在的 csproj" && !graph.ContainsKey(extra.To))
            graph[extra.To] = [];
        return graph;
    }

    // ── 真实图 ────────────────────────────────────────────────────────

    private static Dictionary<string, IReadOnlyList<string>> BuildGraph(string root)
    {
        var csprojs = Directory
            .GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(root, "tests"), "*.csproj", SearchOption.AllDirectories))
            .Where(NotGenerated)
            .ToArray();

        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var csproj in csprojs)
        {
            var from = Norm(Path.GetRelativePath(root, csproj));
            var references = Regex
                .Matches(File.ReadAllText(csproj), @"<ProjectReference\s+Include=""([^""]+)""")
                .Select(m => m.Groups[1].Value.Replace('\\', '/'))
                .Select(r => Norm(Path.GetRelativePath(root,
                    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, r)))))
                .ToList();
            graph[from] = references;
        }
        return graph;
    }

    private static string Norm(string path) => path.Replace('\\', '/');

    private static bool NotGenerated(string path)
        => !path.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
           && !path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
           && !path.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
           && !path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase);

    private static string RepoRoot()
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
        throw new DirectoryNotFoundException("未找到 AgentWorkbench.slnx");
    }
}
