using System.Text;
using System.Text.RegularExpressions;
using AgentWorkbench.ApplicationTests.Runtime;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Docs;

/// <summary>
/// 文档门禁。四条，规则即检查器本身，不另设文档入口：
/// - D1 状态位：每份正式文档头部 15 行内写「适用侧 / 寿命 / 最后校准或建立」三要素。
///   会变的现状、阶段、数量不写进文档；改成指向能现取它的测试 / 探针 / 命令。
/// - D2 相对 md 链接必须指向存在的文件。
/// - D3 局部文档不得重复定义写法（docs/core/ 下）。
/// - D4 正文里的纯文本路径引用（docs/…md、src/…csproj）必须指向存在的文件。
///
/// 取材范围 = docs/** + src/**/README.md；adr/ 与 plans/ 自带生命周期，排除。
/// 四条检查器都是纯函数，另有 D0 用伪造输入证明「检测器能认出违规」——
/// 否则把检查写成永远返回空，D1–D4 也会全绿，那就是最典型的假绿。
/// </summary>
public sealed class DocsHygieneTests
{
    /// <summary>冻结或不属于正式文档集：不要求状态位，也不检查其链接。</summary>
    private static readonly string[] ExcludedFiles =
    [
        "docs/REMADE.md",          // v1 导航，已冻结、明确不作判据
        "docs/产品目标与范围.md",   // 根目录未跟踪的旧副本，不是正本
    ];

    /// <summary>自带生命周期或模板，不套用状态位纪律。</summary>
    private static readonly string[] ExcludedDirs =
    [
        "docs/adr",      // 一个决策一篇，写完不改
        "docs/plans",    // 自带模板，文件名即日期
        "docs/机制层",    // scripts/maf_scan.py 机械生成，无状态位；重跑覆盖
    ];

    private static readonly Regex MdLink = new(@"\]\(([^)\s]+)\)", RegexOptions.Compiled);

    /// <summary>
    /// 反引号内或裸写的文件级路径引用（<c>docs/…md</c> / <c>src/…cs</c> / <c>…csproj</c>）。
    /// 只认「仓库根相对 + 带扩展名」的形态——这是低误报的关键：
    /// 目录名、类名、通配符一律不扫，否则会把 <c>FakeErp</c>、<c>P5</c> 这类词全当引用。
    /// </summary>
    private static readonly Regex PathRef = new(
        @"((?:docs|src)/[A-Za-z0-9_./\u4e00-\u9fff-]+\.(?:md|cs|csproj))",
        RegexOptions.Compiled);

    // ── 检查器（纯函数，便于自证）────────────────────────────────

    private static List<string> StatusViolations(IEnumerable<(string Rel, string Text)> docs)
    {
        var violations = new List<string>();
        foreach (var (rel, text) in docs)
        {
            var head = string.Join("\n", text.Split('\n').Take(15));
            // 要求带 ** 标记：只出现「适用侧」三个字不算（比如改成「适用侧X」仍会被 Contains 命中，
            // 那条变异就验不出来）。这三个标签是给人看的固定格式，不是正文用词。
            if (!head.Contains("**适用侧**", StringComparison.Ordinal))
                violations.Add($"{rel}: 缺「适用侧」标签");
            if (!head.Contains("**寿命**", StringComparison.Ordinal))
                violations.Add($"{rel}: 缺「寿命」标签");
            if (!head.Contains("**最后校准**", StringComparison.Ordinal) &&
                !head.Contains("**建立**", StringComparison.Ordinal))
                violations.Add($"{rel}: 缺「最后校准 / 建立」标签");
        }
        return violations;
    }

    private static List<string> LinkViolations(IEnumerable<(string Rel, string Dir, string Text)> docs)
    {
        var violations = new List<string>();
        foreach (var (rel, dir, text) in docs)
        {
            foreach (Match match in MdLink.Matches(text))
            {
                var target = match.Groups[1].Value;
                if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith('#') ||
                    target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var pathPart = target.Split('#')[0];
                if (!pathPart.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    continue;

                var resolved = Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(pathPart)));
                if (!File.Exists(resolved))
                    violations.Add($"{rel} -> {target}");
            }
        }
        return violations;
    }

    private static List<string> RedefinitionViolations(IEnumerable<(string Rel, string Text)> docs)
    {
        var violations = new List<string>();
        foreach (var (rel, text) in docs)
        {
            if (text.Contains("本文件的写法", StringComparison.Ordinal) ||
                text.Contains("其他子模块照抄", StringComparison.Ordinal))
                violations.Add(rel);
        }
        return violations;
    }

    /// <summary>
    /// 纯文本路径引用必须指向存在的文件。
    /// 这一类腐烂不表现在「内容过期」，而表现在「指向一个不存在的文件」——
    /// md 链接门禁（D2）管不到它，因为它不是链接。2026-11-21 人工扫出 18 处
    /// （6 份 README + 3 处代码注释引用已删除的 <c>docs/core/runtime.md</c>），
    /// 全绿是因为当时没人查。这条把它变成机器可判。
    /// </summary>
    private static List<string> PathRefViolations(IEnumerable<(string Rel, string Text)> docs)
    {
        var root = RepoRoot();
        var violations = new List<string>();
        foreach (var (rel, text) in docs)
        {
            foreach (var line in text.Split('\n'))
            {
                // 显式声明「这个文件不存在」的行是正当用法（如 docs/README 说明正本已删除），
                // 门禁要防的是「以为它还在」，不是「告知它不在」。
                // `git show HEAD:<path>` 是「从历史取内容」的命令，路径本就不在工作区，不是失效引用。
                if (line.Contains("git show", StringComparison.Ordinal) ||
                    line.Contains("不在工作区", StringComparison.Ordinal) ||
                    line.Contains("删除态", StringComparison.Ordinal) ||
                    line.Contains("已移除", StringComparison.Ordinal) ||
                    line.Contains("已删除", StringComparison.Ordinal))
                    continue;

                foreach (Match match in PathRef.Matches(line))
                {
                    var target = match.Groups[1].Value;
                    if (target.Contains('*') || target.Contains('<'))
                        continue;
                    if (File.Exists(Path.Combine(root, target)))
                        continue;
                    violations.Add($"{rel} -> {target}");
                }
            }
        }
        return violations.Distinct(StringComparer.Ordinal).ToList();
    }

    // ── 真实文档集的取材 ────────────────────────────────────────

    private static string RepoRoot() => ContractProbe.FindRepoRoot();

    /// <summary>
    /// 正式文档集 = <c>docs/</c> 全部 md + <c>src/**/README.md</c>。
    /// 后者同样是正式文档（模块 explanation），状态位纪律要求「每份正式文档」，
    /// 不是「docs 下的每份文档」——扫描范围不含 src 时，那 16 份模块 README
    /// 没有任何状态位检查，正是它们最容易变成过期的状态快照。
    /// </summary>
    private static IReadOnlyList<string> FormalDocs()
    {
        var root = RepoRoot();
        var docs = Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories);
        var moduleDocs = Directory.GetFiles(Path.Combine(root, "src"), "README.md", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        return docs.Concat(moduleDocs)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(rel => !ExcludedFiles.Contains(rel, StringComparer.Ordinal))
            .Where(rel => !ExcludedDirs.Any(dir => rel.StartsWith(dir + "/", StringComparison.Ordinal)))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string Rel, string Text) Read(string rel)
        => (rel, File.ReadAllText(Path.Combine(RepoRoot(), rel), Encoding.UTF8));

    // ── D0：门禁自身必须可被证伪 ────────────────────────────────

    [Fact]
    public void D0_四条检查器都能认出伪造的违规()
    {
        Assert.NotEmpty(StatusViolations([("docs/probe.md", "# 探针\n\n正文没有状态位。")]));
        Assert.Empty(StatusViolations([("docs/ok.md", "# ok\n\n- **适用侧**：公共 ｜ **寿命**：常驻 ｜ **最后校准**：2026-11-09")]));

        var docsRoot = Path.Combine(RepoRoot(), "docs");
        Assert.NotEmpty(LinkViolations([("docs/probe.md", docsRoot, "[不存在](__no_such_file.md)")]));
        Assert.Empty(LinkViolations([("docs/probe.md", docsRoot, "[存在](core/工具执行.md)")]));

        Assert.NotEmpty(RedefinitionViolations([("docs/core/probe.md", "## 本文件的写法\n1. …")]));
        Assert.Empty(RedefinitionViolations([("docs/core/probe.md", "## 写法\n\n见 docs/core/ 各部件文档。")]));

        Assert.NotEmpty(PathRefViolations([("src/Probe/README.md", "见 `docs/__no_such_doc.md`。")]));
        Assert.Empty(PathRefViolations([("src/Probe/README.md", "见 `docs/host/运行期流程.md`。")]));
    }

    // ── D1–D4：真实文档集 ──────────────────────────────────────

    [Fact]
    public void D1_正式文档头部必须有状态位三要素()
    {
        var violations = StatusViolations(FormalDocs().Select(Read));
        Assert.True(violations.Count == 0,
            "状态位缺失（三要素：适用侧 / 寿命 / 最后校准或建立）：\n" + string.Join("\n", violations));
    }

    [Fact]
    public void D2_文档内的相对md链接目标必须存在()
    {
        var root = RepoRoot();
        var docs = FormalDocs().Select(rel =>
        {
            var full = Path.Combine(root, rel);
            return (rel, Path.GetDirectoryName(full)!, File.ReadAllText(full, Encoding.UTF8));
        });
        var violations = LinkViolations(docs);
        Assert.True(violations.Count == 0,
            "链接指向不存在的文件（工作区没有但 HEAD 有的，写纯文本不写链接）：\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void D3_局部文档不得重复定义写作规范()
    {
        var root = RepoRoot();
        var docs = FormalDocs()
            .Where(rel => rel.StartsWith("docs/core/", StringComparison.Ordinal))
            .Select(Read);
        var violations = RedefinitionViolations(docs);
        Assert.True(violations.Count == 0,
            "局部文档重复定义写作规范（写法只在门禁与部件文档各司其职，不在此重定义）：" + string.Join(", ", violations));
    }

    [Fact]
    public void D4_正文里引用的文件路径必须存在()
    {
        var violations = PathRefViolations(FormalDocs().Select(Read));
        Assert.True(violations.Count == 0,
            "正文引用了不存在的路径（纯文本引用不在 D2 范围内，故单列）；" +
            "文件改名 / 删除后必须同一次提交里改掉引用，或改成「跑某命令现取」：\n" +
            string.Join("\n", violations));
    }
}
