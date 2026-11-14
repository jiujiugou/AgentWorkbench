using System.Text;
using System.Text.RegularExpressions;
using AgentWorkbench.ApplicationTests.Runtime;
using Xunit;

namespace AgentWorkbench.ApplicationTests.Docs;

/// <summary>
/// 文档门禁（docs/README.md §7）。三条：状态位、相对链接、局部文档不得重定义写法。
///
/// 三条检查器都是纯函数，另有 D0 用伪造输入证明「检测器能认出违规」——
/// 否则把检查写成永远返回空，D1–D3 也会全绿，那就是最典型的假绿。
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

    // ── 真实文档集的取材 ────────────────────────────────────────

    private static string RepoRoot() => ContractProbe.FindRepoRoot();

    private static IReadOnlyList<string> FormalDocs()
    {
        var root = RepoRoot();
        return Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
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
    public void D0_三条检查器都能认出伪造的违规()
    {
        Assert.NotEmpty(StatusViolations([("docs/probe.md", "# 探针\n\n正文没有状态位。")]));
        Assert.Empty(StatusViolations([("docs/ok.md", "# ok\n\n- **适用侧**：公共 ｜ **寿命**：常驻 ｜ **最后校准**：2026-11-09")]));

        var docsRoot = Path.Combine(RepoRoot(), "docs");
        Assert.NotEmpty(LinkViolations([("docs/probe.md", docsRoot, "[不存在](__no_such_file.md)")]));
        Assert.Empty(LinkViolations([("docs/probe.md", docsRoot, "[存在](../docs/README.md)")]));

        Assert.NotEmpty(RedefinitionViolations([("docs/core/probe.md", "## 本文件的写法\n1. …")]));
        Assert.Empty(RedefinitionViolations([("docs/core/probe.md", "## 写法\n\n见 README §6。")]));
    }

    // ── D1–D3：真实文档集 ──────────────────────────────────────

    [Fact]
    public void D1_正式文档头部必须有状态位三要素()
    {
        var violations = StatusViolations(FormalDocs().Select(Read));
        Assert.True(violations.Count == 0,
            "状态位缺失（见 docs/README.md §5）：\n" + string.Join("\n", violations));
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
            "局部文档重复定义写作规范（应只留在 docs/README.md §6）：" + string.Join(", ", violations));
    }
}
