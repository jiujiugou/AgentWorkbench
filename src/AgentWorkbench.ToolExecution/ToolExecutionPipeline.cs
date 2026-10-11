using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.ToolExecution;

/// <summary>一次执行的工具预算（由执行控制 AgentLoop 决定）。</summary>
public sealed record ToolExecutionLimits(int MaxIterations);

/// <summary>一次工具调用的轨迹（迭代号 / 工具名 / 参数 / 结果 / 失败原因）。</summary>
/// <remarks>
/// <see cref="Result"/> 与 <see cref="Error"/> 互斥：调用成功时只有 <see cref="Result"/>，
/// 工具抛异常（含被取消）时 <see cref="Result"/> 为 null、<see cref="Error"/> 记下异常类型与消息。
/// 没有 <see cref="Error"/> 时，失败的调用在轨迹里与「返回了空」完全无法区分。
/// </remarks>
public sealed record ToolCallTrace(int Iteration, string Name, string? Arguments, string? Result, string? Error = null);

/// <summary>
/// 一次「带工具的执行」：把模型生成的工具调用变成真实调用，并留下轨迹。
/// <see cref="Client"/> 交给上层去建 Agent；<see cref="Traces"/> 在执行过程中持续追加。
/// </summary>
public sealed class ToolExecutionRun
{
    private readonly List<ToolCallTrace> _traces = [];

    internal ToolExecutionRun(IChatClient client) => Client = client;

    /// <summary>已装配函数调用环（MaximumIterations = 预算）的客户端。</summary>
    public IChatClient Client { get; }

    /// <summary>实时轨迹视图（执行中可读，用于超时后取回已完成的部分）。</summary>
    public IReadOnlyList<ToolCallTrace> Traces => _traces;

    internal int BeginCall(ToolCallTrace trace)
    {
        _traces.Add(trace);
        return _traces.Count - 1;
    }

    internal void CompleteCall(int index, string? result, string? error = null)
        => _traces[index] = _traces[index] with { Result = result, Error = error };
}

/// <summary>工具执行管道：把模型生成的工具调用转换为真实执行，并把结果回填给模型。</summary>
public interface IToolExecutionPipeline
{
    ToolExecutionRun Create(IChatClient innerClient, ToolExecutionLimits limits);
}

/// <inheritdoc />
public sealed class ToolExecutionPipeline : IToolExecutionPipeline
{
    private readonly ToolExecutionOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;

    public ToolExecutionPipeline(
        IOptions<ToolExecutionOptions> options,
        ILoggerFactory loggerFactory,
        IServiceProvider services)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _services = services;
    }

    public ToolExecutionRun Create(IChatClient innerClient, ToolExecutionLimits limits)
    {
        var invoking = new FunctionInvokingChatClient(innerClient, _loggerFactory, _services)
        {
            MaximumIterationsPerRequest = limits.MaxIterations
        };

        var run = new ToolExecutionRun(invoking);
        invoking.FunctionInvoker = async (context, token) =>
        {
            var index = run.BeginCall(new ToolCallTrace(
                context.Iteration, context.Function.Name, Serialize(context.Arguments), null));
            try
            {
                var result = await context.Function.InvokeAsync(context.Arguments, token);
                run.CompleteCall(index, Truncate(result?.ToString()));
                return result;
            }
            catch (Exception ex)
            {
                // 只记录、不改语义：异常原样抛回 MEA（由它决定转成给模型的错误结果还是向外传播取消）。
                // 记录必须在这一层做 —— 异常一旦进了 MEA，就再也回不到这里了。
                run.CompleteCall(index, result: null, error: Truncate($"{ex.GetType().Name}: {ex.Message}"));
                throw;
            }
        };

        return run;
    }

    private static string Serialize(AIFunctionArguments arguments)
        => JsonSerializer.Serialize(arguments.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()));

    private string? Truncate(string? value)
        => value is null || value.Length <= _options.MaxResultChars
            ? value
            : value[.._options.MaxResultChars];
}
