using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace AgentWorkbench.ArchitectureTests;

/// <summary>
/// 授权姿态的结构断言：<b>每个 SignalR Hub 必须显式声明 <c>[Authorize]</c> 或 <c>[AllowAnonymous]</c></b>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：Hub 现在靠 <c>Program.cs</c> 的 <c>FallbackPolicy</c>（全局默认拒）被动受保护 ——
/// 2026-11-16 实测，未登录 <c>POST /hubs/agent/negotiate</c> 与直连 WS 接入点
/// <c>GET /hubs/agent?id=...</c>（跳过协商）都返回 401，所以「现在没有洞」。
/// </para>
/// <para>
/// 但那是配置，不是代码契约。改掉那条全局策略（文件里已有 <c>MapFallback(...).AllowAnonymous()</c>
/// 这种「为某处破例」的先例），Hub 会静默失守，而 S01 的测试是直接 <c>new AgentHub(...)</c>
/// 不走管道的，一条都不会红。把姿态钉成结构事实，改动就必须有人看见。
/// </para>
/// <para>
/// <b>这是弱一档的断言</b>：扫源码文本，不是编译期依赖（本项目的设计前提是零生产项目引用）。
/// 它能守住「有没有写」，守不住「写了但是写得对不对」——后者归 S02 的行为判据。
/// </para>
/// </remarks>
public sealed class HubAuthorizationTests
{
    /// <summary>类声明：<c>public sealed class Xxx : Hub</c>（含 <c>Hub&lt;T&gt;</c> 与后续接口）。</summary>
    private static readonly Regex HubDeclaration = new(
        @"^[ \t]*public[ \t]+(?:(?:sealed|abstract|partial)[ \t]+)*class[ \t]+(?<name>\w+)[ \t]*:[ \t]*Hub\b",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>授权特性：<c>[Authorize]</c> / <c>[Authorize(Roles=...)]</c> / <c>[AllowAnonymous]</c>。</summary>
    private static readonly Regex AuthAttribute = new(
        @"\[[ \t]*(?:Authorize|AllowAnonymous)\b",
        RegexOptions.Compiled);

    // 负控：删掉 AgentHub 上的 [Authorize]（或改名成别的特性）→ 本条红，
    // 报「以下 Hub 授权姿态是隐式的」。已实跑自证后恢复。
    [Fact]
    public void Hub_必须显式声明授权姿态()
    {
        var root = FindRepoRoot();
        var offenses = new List<string>();
        var scanned = 0;

        foreach (var file in Directory.EnumerateFiles(
            Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
                continue;

            var text = File.ReadAllText(file);
            foreach (Match match in HubDeclaration.Matches(text))
            {
                scanned++;

                // 该类型的「头部」= 上一个类型结束的 "}" 之后到 class 关键字之前。
                // 只在这段里找特性，避免把别的类的 [Authorize] 算进来。
                var head = text[..match.Index];
                var cut = head.LastIndexOf("\n}", StringComparison.Ordinal);
                var region = cut >= 0 ? head[(cut + 2)..] : head;

                if (!AuthAttribute.IsMatch(StripComments(region)))
                    offenses.Add($"{Path.GetRelativePath(root, file)} → {match.Groups["name"].Value}");
            }
        }

        // 恒真的绿等于没验：一条 Hub 都没扫到，说明规则或路径失效了。
        Assert.True(scanned > 0, "没有扫到任何 Hub —— 扫描规则或 src 路径失效，这条断言此刻是恒真的。");

        Assert.True(
            offenses.Count == 0,
            "以下 Hub 既没有 [Authorize] 也没有 [AllowAnonymous]，授权姿态是隐式的（只靠全局 FallbackPolicy）：\n  "
            + string.Join("\n  ", offenses)
            + "\n要么在类上加 [Authorize]，要么显式 [AllowAnonymous] 并写明为什么可以匿名。");
    }

    private static bool IsBuildOutput(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}obj{sep}") || path.Contains($"{sep}bin{sep}");
    }

    /// <summary>去掉 <c>//</c> 行注释与 <c>/* */</c> 块注释，只留代码。</summary>
    private static string StripComments(string text)
    {
        var noBlock = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return string.Join("\n", noBlock.Split('\n').Select(line =>
        {
            var i = line.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? line[..i] : line;
        }));
    }

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
}
