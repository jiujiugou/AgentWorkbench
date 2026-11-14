using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Runtime.Loop;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace AgentWorkbench.CoreTests.Runtime;

/// <summary>
/// ② 循环驱动的装配判据：全部决定集中在 <see cref="LoopAssembly"/>，且真的落到
/// MEA 的 FunctionInvokingChatClient 上，不是「注释里写了」「框架默认恰好如此」。
/// </summary>
/// <remarks>
/// 期望值<b>故意硬编码</b> 3 / false / false：若拿 LoopAssembly 的常量当期望，
/// 改常量时两边一起变、断言恒绿。改决定必须连这份判据一起改，这是有意的成本。
/// </remarks>
public sealed class 循环驱动装配
{
    [Fact]
    public void 装配决定由Harness一处产出_预算来自Loop()
    {
        var loop = new Mock<IAgentLoop>();
        loop.Setup(x => x.MaxIterations).Returns(6);
        var guard = new Mock<IAgentRunGuard>().Object;

        var limits = LoopAssembly.CreateLimits(loop.Object, guard, null, null);

        Assert.Equal(6, limits.MaxIterations);          // ③ 定义预算
        Assert.Equal(3, limits.MaxConsecutiveErrorsPerRequest);
        Assert.False(limits.AllowConcurrentInvocation);
        Assert.False(limits.TerminateOnUnknownCalls);
        Assert.Same(guard, limits.Guard);
    }

    [Fact]
    public void 四个选项真的落到框架的函数调用环上()
    {
        var loop = new Mock<IAgentLoop>();
        loop.Setup(x => x.MaxIterations).Returns(6);
        var limits = LoopAssembly.CreateLimits(loop.Object, new Mock<IAgentRunGuard>().Object, null, null);

        var run = CreatePipeline().Create(new Mock<IChatClient>().Object, limits);
        var ficc = Assert.IsType<FunctionInvokingChatClient>(run.Client);

        // 真触达：读框架实例的属性，不是扫源码字符串。
        Assert.Equal((int?)6, ficc.MaximumIterationsPerRequest);
        Assert.Equal((int?)3, ficc.MaximumConsecutiveErrorsPerRequest);
        Assert.False(ficc.AllowConcurrentInvocation);
        Assert.False(ficc.TerminateOnUnknownCalls);
    }

    // 负控：把 LoopAssembly 的任一常量改掉 → 上一条与这一条都必须红。
    [Fact]
    public void 限额的默认值与LoopAssembly的决定一致_两处不得漂移()
    {
        var defaults = new ToolExecutionLimits(6);

        Assert.Equal(3, defaults.MaxConsecutiveErrorsPerRequest);
        Assert.False(defaults.AllowConcurrentInvocation);
        Assert.False(defaults.TerminateOnUnknownCalls);
    }

    [Fact]
    public void Harness的两处装配都走LoopAssembly_不再散着填框架选项()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Core", "Runtime", "AgentWorkbench.Runtime.Harness", "AgentHarnessService.cs"));
        var code = CodeLines(source);

        Assert.Contains(code, line => line.Contains("LoopAssembly.CreateLimits"));
        Assert.Contains(code, line => line.Contains("UseProvidedChatClientAsIs = LoopAssembly.UseProvidedChatClientAsIs"));
        // 不再直接 new 限额：装配决定只有 LoopAssembly 一个产地。
        Assert.DoesNotContain(code, line => line.Contains("new ToolExecutionLimits("));
    }

    [Fact]
    public void 装配约定的开关仍是代码行_不是只剩注释()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Core", "Runtime", "AgentWorkbench.Runtime.Harness", "AgentHarnessService.cs"));
        var code = CodeLines(source);

        Assert.Contains(code, line => line.Contains("UseProvidedChatClientAsIs"));
        Assert.DoesNotContain(code, line => line.Contains("AIContextProviders"));
    }

    // ── 辅助 ──────────────────────────────────────────────────────────

    private static ToolExecutionPipeline CreatePipeline()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        return new ToolExecutionPipeline(
            Options.Create(new ToolExecutionOptions()),
            new AllowAll(),
            NullLoggerFactory.Instance,
            services);
    }

    private sealed class AllowAll : IToolApprovalPolicy
    {
        public ToolApprovalMode Decision(string toolName) => ToolApprovalMode.Auto;
    }

    private static IReadOnlyList<string> CodeLines(string source)
        => source.Split('\n')
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal)
                        && !line.StartsWith("/*", StringComparison.Ordinal)
                        && !line.StartsWith("*", StringComparison.Ordinal))
            .ToList();

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
