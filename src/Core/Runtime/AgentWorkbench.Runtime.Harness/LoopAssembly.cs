using AgentWorkbench.Runtime.Loop;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Shared;

namespace AgentWorkbench.Runtime.Harness;

/// <summary>
/// ② 循环驱动的<b>全部装配决定</b>，唯一产地在 Harness —— 这是本格「自己的家」。
/// </summary>
/// <remarks>
/// ② 没有独立模块：循环本体是 MEA 的 <c>FunctionInvokingChatClient</c>（由 ④ 的管道创建），
/// Agent 对象由 MAF 的 <c>ChatClientAgent</c> 提供。本仓能决定的只有「怎么把它们拼起来」，
/// 过去这些决定散在 <c>PrepareRunAsync</c>、<c>BuildAgent</c> 和管道构造式里，改一处容易漏另一处。
/// 这里把它们收成一处：常量给出值，<see cref="CreateLimits"/> 产出给 ④ 的限额，
/// <see cref="UseProvidedChatClientAsIs"/> 给出给 MAF 的装配开关。
/// <para>
/// <b>为什么值等于 MEA 10.10 的默认值</b>：落实 ② 不改变任何现有循环行为，只把「框架隐式默认」
/// 变成「本仓显式决定」。这样升级 MEA 时，即便框架改了默认值，本仓行为也不会跟着变——
/// 这是 <c>docs/core/循环驱动.md</c> 缺口 B 的正解，不是加一个可配置开关。
/// </para>
/// <para>
/// <b>为什么是 public</b>：判据（CoreTests）要读这些常量，断言「决定真的落到框架的
/// FunctionInvokingChatClient 属性上」。它不是给应用层的配置口，改这里的值要连带改测试。
/// </para>
/// </remarks>
public static class LoopAssembly
{
    /// <summary>连续多少轮工具调用报错后，框架才中止循环。MEA 10.10 默认 3。</summary>
    public const int MaxConsecutiveErrorsPerRequest = 3;

    /// <summary>一轮里模型请求多个工具时是否并行执行。选 false：串行让轨迹顺序与迭代号可重建。</summary>
    public const bool AllowConcurrentInvocation = false;

    /// <summary>遇到未知工具名是否直接终止循环。选 false：回填「找不到」让模型自我纠正，④ 的核对层负责留痕。</summary>
    public const bool TerminateOnUnknownCalls = false;

    /// <summary>
    /// 让 MAF 原样使用我们这条已装好工具环的 client，而不是在环外再包一层函数调用环。
    /// </summary>
    /// <remarks>
    /// 这是<b>装配约定</b>，不是已证实的硬要求：2026-11-13 实测在 MEA 10.10.0 +
    /// 传入的 client 已是 FunctionInvokingChatClient 的条件下，删掉它没有可观测差异
    /// （① 的六条判据、② 的两轮回填、F1.7 全部仍绿）。保留它是因为它表达了意图，
    /// 且可能在别的 MAF 版本或 client 形态下有意义；「会绕过守卫与轨迹」这一因果说法
    /// 目前没有行为证据，别当成已证实的事实。
    /// </remarks>
    public const bool UseProvidedChatClientAsIs = true;

    /// <summary>
    /// 产出 ④ 工具执行环的限额：迭代预算来自 ③（<see cref="IAgentLoop.MaxIterations"/>），
    /// 循环驱动的四项决定来自本类。两条出口（RunAsync / StreamAsync）共用这一份。
    /// </summary>
    public static ToolExecutionLimits CreateLimits(
        IAgentLoop loop, IAgentRunGuard guard, IToolApprover? approver, IToolResultVeto? veto)
        => new(
            loop.MaxIterations,
            guard,
            approver,
            veto,
            MaxConsecutiveErrorsPerRequest,
            AllowConcurrentInvocation,
            TerminateOnUnknownCalls);
}
