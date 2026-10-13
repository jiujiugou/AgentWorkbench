using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.ToolExecution;

/// <summary>一次执行的工具预算（由执行控制 AgentLoop 决定）。</summary>
/// <param name="MaxIterations">工具环上限（落到 MEA 的 <c>MaximumIterationsPerRequest</c>）。</param>
/// <param name="Guard">
/// 执行控制守卫（重复调用 / token 预算）；为 <c>null</c> 时环内不做任何提前终止判定。
/// </param>
/// <param name="Approver">
/// 审批执行者（run 级）：<c>Ask</c> 级工具靠它去问人；为 <c>null</c> 时任何 <c>Ask</c> 都按拒绝处理
/// （fail-closed —— 无人可问就不放行）。
/// </param>
public sealed record ToolExecutionLimits(
    int MaxIterations, IAgentRunGuard? Guard = null, IToolApprover? Approver = null);

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
    private readonly List<Exception> _toolFailures = [];

    internal ToolExecutionRun(IChatClient client) => Client = client;

    /// <summary>已装配函数调用环（MaximumIterations = 预算）的客户端。</summary>
    public IChatClient Client { get; }

    /// <summary>实时轨迹视图（执行中可读，用于超时后取回已完成的部分）。</summary>
    public IReadOnlyList<ToolCallTrace> Traces => _traces;

    /// <summary>
    /// 执行控制判定的提前终止原因（重复调用 / 预算耗尽）；没有提前终止时为 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 收口方式是把这一次工具调用标记为终止（<c>FunctionInvocationContext.Terminate</c>）并让框架正常返回，
    /// 因此终止原因不可能从返回值或异常里看出来 —— 必须在这里显式留下。
    /// </remarks>
    public AgentRunStop? Stop { get; private set; }

    /// <summary>工具实际抛出的异常实例（用于把冒泡到编排层的异常归类成「工具异常」）。</summary>
    public IReadOnlyList<Exception> ToolFailures => _toolFailures;

    /// <summary>
    /// 工具调用完成时触发（成功 / 失败 / 被守卫终止都触发），在函数调用环内同步抛出——
    /// 订阅方只应做非阻塞写入（如 Channel.TryWrite），否则会拖住模型循环。
    /// </summary>
    /// <remarks>
    /// 事件里的 <see cref="ToolCallTrace.Result"/> 是<b>未截断</b>的完整结果（供流式出口做实时桥接，
    /// 如知识库来源面板）；轨迹 <see cref="Traces"/> 内仍是截断版（<see cref="ToolExecutionOptions.MaxResultChars"/>，
    /// 回放 / 判分用）——两者刻意不同：大结果不该进账本，但要能到界面。
    /// </remarks>
    public event Action<ToolCallTrace>? ToolCompleted;

    internal int BeginCall(ToolCallTrace trace)
    {
        _traces.Add(trace);
        return _traces.Count - 1;
    }

    internal void CompleteCall(int index, string? result, string? error = null, string? fullResult = null)
    {
        var completed = _traces[index] with { Result = result, Error = error };
        _traces[index] = completed;
        // 事件payload 用完整结果；轨迹本体保持截断语义不变。
        ToolCompleted?.Invoke(completed with { Result = fullResult ?? result });
    }

    internal void StopAt(AgentRunStop stop) => Stop = stop;

    internal void MarkToolFailure(Exception exception) => _toolFailures.Add(exception);
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
    private readonly IToolApprovalPolicy _approval;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _services;

    public ToolExecutionPipeline(
        IOptions<ToolExecutionOptions> options,
        IToolApprovalPolicy approval,
        ILoggerFactory loggerFactory,
        IServiceProvider services)
    {
        _options = options.Value;
        _approval = approval;
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
            var arguments = Serialize(context.Arguments);
            var index = run.BeginCall(new ToolCallTrace(
                context.Iteration, context.Function.Name, arguments, null));

            // 执行控制：判「重复调用」与「token 预算」的信息只有到环内、调用发出时才有，
            // 判定必须发生在真正执行之前 —— 否则「省钱」就成了事后追认。
            var stop = limits.Guard?.InspectToolCall(
                new ToolCallObservation(context.Iteration, context.Function.Name, arguments));
            if (stop is not null)
            {
                run.CompleteCall(index, result: null, error: $"{stop.Code}: {stop.Message}");
                run.StopAt(stop);
                // 干净收口：这一轮不再回问模型（抛异常会被框架当成「工具失败」喂回模型）。
                context.Terminate = true;
                return null;
            }

            // 第二维（审批）：在白名单「能不能用」之外，再决定「这次调用要不要先问人」。
            // 顺序放在守卫之后：守卫判的是「整轮要不要停」（循环 / 预算），是更上层的止损，
            // 已经决定要停的调用不必再走审批。
            var denial = await InspectApprovalAsync(limits, context.Function.Name, arguments, token);
            if (denial is not null)
            {
                // 拒绝：工具绝不执行，把「被拒」当作这次调用的结果回填给模型 ——
                // 模型据此换路或收口，而不是反复重试同一个被拒的调用。
                // 轨迹里 result 是结构化拒绝载荷、error 是归类码，回放时「被拒」与「工具失败」分得开。
                var payload = DenialPayload(context.Function.Name, denial);
                run.CompleteCall(index, Truncate(payload), error: denial, fullResult: payload);
                return payload;
            }

            try
            {
                var result = await context.Function.InvokeAsync(context.Arguments, token);
                var text = result?.ToString();
                // 轨迹存截断版（省账本），事件带完整版（来源面板要用）。
                run.CompleteCall(index, Truncate(text), fullResult: text);
                return result;
            }
            catch (Exception ex)
            {
                // 只记录、不改语义：异常原样抛回 MEA（由它决定转成给模型的错误结果还是向外传播取消）。
                // 记录必须在这一层做 —— 异常一旦进了 MEA，就再也回不到这里了。
                run.MarkToolFailure(ex);
                run.CompleteCall(index, result: null, error: Truncate($"{ex.GetType().Name}: {ex.Message}"));
                throw;
            }
        };

        return run;
    }

    /// <summary>
    /// 审批闸门：返回 <c>null</c> = 放行执行；返回归类码 = 拒绝（工具不得执行）。
    /// </summary>
    /// <remarks>
    /// <c>Auto</c> 不触碰审批者（不打扰、不引入通道故障面）；<c>Deny</c> 直接拒；
    /// <c>Ask</c> 必须拿到明确批准，其余一切情况（无审批者 / 超时 / 通道故障）一律按拒绝处理 ——
    /// 安全闸坏了必须往「不放行」倒，绝不能因为「没人回答」就当成同意。
    /// </remarks>
    private async Task<string?> InspectApprovalAsync(
        ToolExecutionLimits limits, string toolName, string? arguments, CancellationToken token)
    {
        var mode = _approval.Decision(toolName);

        if (mode == ToolApprovalMode.Auto)
            return null;

        if (mode == ToolApprovalMode.Deny)
            return ToolApprovalCodes.PolicyDenied;

        // Ask：无人可问 = 不放行。
        if (limits.Approver is null)
            return ToolApprovalCodes.Unavailable;

        var timeout = _options.ApprovalTimeoutSeconds <= 0
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromSeconds(_options.ApprovalTimeoutSeconds);

        try
        {
            var approved = await limits.Approver
                .RequestAsync(new ToolApprovalRequest(toolName, arguments), token)
                .WaitAsync(timeout, token);
            return approved ? null : ToolApprovalCodes.UserDenied;
        }
        catch (TimeoutException)
        {
            // 超时即拒绝：审批是安全闸，等不到答复不等于默认同意。
            return ToolApprovalCodes.Unavailable;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 调用方取消是取消本身，不是审批结论 —— 原样抛，由上层按取消归类。
            throw;
        }
        catch (Exception)
        {
            // 审批通道自身故障同样按拒绝处理（fail-closed）。
            return ToolApprovalCodes.Unavailable;
        }
    }

    /// <summary>拒绝载荷：结构化的「被拒」结果，回填给模型也进轨迹（截断后）。</summary>
    private static string DenialPayload(string toolName, string code)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["denied"] = "true",
            ["tool"] = toolName,
            ["reason"] = code
        });

    /// <summary>序列化工具参数，并按参数名排序 —— 键序稳定，重复调用的指纹才可比。</summary>
    private static string Serialize(AIFunctionArguments arguments)
        => JsonSerializer.Serialize(arguments
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()));

    private string? Truncate(string? value)
        => value is null || value.Length <= _options.MaxResultChars
            ? value
            : value[.._options.MaxResultChars];
}
