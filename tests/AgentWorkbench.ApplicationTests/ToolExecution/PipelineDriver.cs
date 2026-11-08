using System.Collections;
using System.Reflection;
using System.Text;
using AgentWorkbench.ApplicationTests.Support;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.ApplicationTests.ToolExecution;

/// <summary>
/// 把 <see cref="ToolExecutionPipeline"/> 装配成「可执行工具环」的测试驱动。
/// 只做三件事：造管线、造可计数的 AITool、用 <see cref="ChatOptions.Tools"/> 驱动 run.Client。
/// 所有断言仍落在测试文件里，本文件不替测试下结论。
/// </summary>
internal static class PipelineDriver
{
    public static ToolExecutionPipeline NewPipeline(
        IToolApprovalPolicy approval,
        int maxResultChars = 400,
        int approvalTimeoutSeconds = 120)
        => new(
            Options.Create(new ToolExecutionOptions
            {
                MaxResultChars = maxResultChars,
                ApprovalTimeoutSeconds = approvalTimeoutSeconds
            }),
            approval,
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());

    /// <summary>
    /// 跑一次完整工具环：模型第 1 轮返回工具调用（脚本决定），结果回填后再问模型（脚本决定）。
    /// 工具异常会冒泡出 GetResponseAsync，这里捕获后仍把 run 交回测试，便于检查轨迹与 ToolFailures。
    /// </summary>
    public static async Task<DriveResult> DriveAsync(
        ScriptedChatClient inner,
        AITool tool,
        IToolApprovalPolicy approval,
        IToolApprover? approver = null,
        IAgentRunGuard? guard = null,
        IToolResultVeto? veto = null,
        int maxIterations = 4,
        int maxResultChars = 400,
        int approvalTimeoutSeconds = 120)
    {
        var pipeline = NewPipeline(approval, maxResultChars, approvalTimeoutSeconds);
        var run = pipeline.Create(inner, new ToolExecutionLimits(maxIterations, guard, approver, veto));

        Exception? thrown = null;
        try
        {
            await run.Client.GetResponseAsync(
                new List<ChatMessage> { new(ChatRole.User, "开始") },
                new ChatOptions { Tools = new List<AITool> { tool } },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        return new DriveResult(run, inner, thrown);
    }
}

internal sealed record DriveResult(ToolExecutionRun Run, ScriptedChatClient Inner, Exception? Thrown);

/// <summary>可计数、可控结果、可抛异常的测试工具；名字固定便于脚本按名调用。</summary>
internal sealed class CountingTool
{
    public int Executions { get; private set; }
    public string Name { get; }
    public AITool Tool { get; }

    // 表达式 lambda，与 GetCurrentTimeTool 的 AIFunctionFactory.Create(() => ..., name, desc) 同构
    private CountingTool(string name, Func<string> body)
    {
        Name = name;
        Tool = AIFunctionFactory.Create(
            () => CountAndRun(body),
            name,
            "测试工具");
    }

    // 表达式 lambda + 显式参数，与 CalculatorTool 的 AIFunctionFactory.Create((string x) => ..., name, desc) 同构
    private CountingTool(string name, Func<string, string> body)
    {
        Name = name;
        Tool = AIFunctionFactory.Create(
            (string input) => CountAndRun(body, input),
            name,
            "测试工具");
    }

    private string CountAndRun(Func<string> body)
    {
        Executions++;
        return body();
    }

    private string CountAndRun(Func<string, string> body, string input)
    {
        Executions++;
        return body(input);
    }

    public static CountingTool NoArg(string name, Func<string> body) => new(name, body);

    public static CountingTool OneArg(string name, Func<string, string> body) => new(name, body);
}

/// <summary>固定审批级别的策略替身（不依赖配置热重载，测试里直接给结论）。</summary>
internal sealed class FixedApprovalPolicy : IToolApprovalPolicy
{
    private readonly ToolApprovalMode _default;
    private readonly IReadOnlyDictionary<string, ToolApprovalMode> _overrides;

    public FixedApprovalPolicy(ToolApprovalMode mode)
        : this(mode, new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal))
    {
    }

    public FixedApprovalPolicy(ToolApprovalMode mode, IReadOnlyDictionary<string, ToolApprovalMode> overrides)
    {
        _default = mode;
        _overrides = overrides;
    }

    public ToolApprovalMode Decision(string toolName)
        => _overrides.TryGetValue(toolName, out var m) ? m : _default;
}

/// <summary>审批执行者替身：可批准 / 拒绝 / 抛通道故障 / 永不返回（配合超时）。</summary>
internal sealed class StubApprover : IToolApprover
{
    private readonly Func<ToolApprovalRequest, CancellationToken, Task<bool>> _handler;

    public int Calls { get; private set; }

    public StubApprover(bool approved)
        : this((_, _) => Task.FromResult(approved))
    {
    }

    public StubApprover(Func<ToolApprovalRequest, CancellationToken, Task<bool>> handler)
        => _handler = handler;

    public Task<bool> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        return _handler(request, cancellationToken);
    }
}

/// <summary>
/// 用反射读取「模型本次收到的消息」里的字符串内容，不直接依赖 FunctionResultContent 等具体类型。
/// 供「拒绝载荷是否回填模型」「进入模型的结果长度」这类断言使用。
/// </summary>
internal static class RequestInspector
{
    /// <summary>把请求里所有 AIContent 的字符串属性值拼成一段文本。</summary>
    public static string Dump(IReadOnlyList<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var content in EnumerateContents(messages))
            foreach (var value in StringValuesOf(content))
                sb.AppendLine(value);
        return sb.ToString();
    }

    /// <summary>请求里最长的字符串属性值长度（工具结果通常就是最长的那段）。</summary>
    public static int LongestStringLength(IReadOnlyList<ChatMessage> messages)
    {
        var max = 0;
        foreach (var content in EnumerateContents(messages))
            foreach (var value in StringValuesOf(content))
                max = Math.Max(max, value.Length);
        return max;
    }

    private static IEnumerable<object> EnumerateContents(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.GetType().GetProperty("Contents")?.GetValue(message) is IEnumerable contents)
                foreach (var content in contents)
                    if (content is not null)
                        yield return content;
        }
    }

    private static IEnumerable<string> StringValuesOf(object content)
    {
        foreach (var property in content.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            object? value;
            try
            {
                if (property.GetIndexParameters().Length != 0)
                    continue;
                value = property.GetValue(content);
            }
            catch (Exception)
            {
                continue;
            }

            if (value is string text)
                yield return text;
        }
    }
}
