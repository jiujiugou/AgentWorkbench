using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentWorkbench.Runtime.Tools;

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
    int MaxIterations,
    IAgentRunGuard? Guard = null,
    IToolApprover? Approver = null,
    IToolResultVeto? Veto = null);

/// <summary>
/// 结果否决：工具已经返回之后，问一句「这次结果能不能作为依据」；不能就终止这一轮生成。
/// </summary>
/// <remarks>
/// 与 <see cref="IAgentRunGuard"/> 是同一件事的两个时机：守卫看调用前（重复调用 / 预算），
/// 否决看调用后（结果内容）。分开是因为判据来源不同——守卫只需要调用指纹，否决要读结果本身。
/// <para>
/// 契约放在这里而不是领域模块：执行管道不认识"知识库"或"资料不足"，它只知道有人要求停。
/// 领域侧实现这个接口，把判定结果翻译成终止原因。
/// </para>
/// </remarks>
public interface IToolResultVeto
{
    /// <summary>
    /// 工具返回后询问。<paramref name="result"/> 是未截断的完整输出（与轨迹里的截断版不同）。
    /// </summary>
    /// <returns>返回非 <c>null</c> 表示必须终止：该轮不再回问模型，直接按此原因收口。</returns>
    AgentRunStop? Inspect(string toolName, string? result);
}

/// <summary>参数结构校验失败的归类码：与「工具异常」「审批拒绝」分开，回放时能归因。</summary>
public static class ToolValidationCodes
{
    public const string ArgumentValidationFailed = "argument_validation_failed";
}

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
    private readonly List<string> _unknownCalls = [];

    internal ToolExecutionRun(IChatClient client) => Client = client;

    /// <summary>已装配函数调用环（MaximumIterations = 预算）的客户端。</summary>
    public IChatClient Client { get; }

    /// <summary>实时轨迹视图（执行中可读，用于超时后取回已完成的部分）。</summary>
    public IReadOnlyList<ToolCallTrace> Traces => _traces;

    /// <summary>
    /// 模型请求过、但不在本次工具清单里的名字。
    /// </summary>
    /// <remarks>
    /// 这些调用<b>不会</b>出现在 <see cref="Traces"/> 里：回填「工具找不到」发生在
    /// <see cref="FunctionInvokingChatClient"/> 内部，走不到我们的 <c>FunctionInvoker</c>。
    /// 不单独记下来，事后就查不到「模型曾经请求过一个不存在的工具」——
    /// 而「谁 / 何时 / 看到什么」必须答得出来。
    /// </remarks>
    public IReadOnlyList<string> UnknownCalls => _unknownCalls;

    internal void MarkUnknownCall(string name) => _unknownCalls.Add(name);

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
        // 核对层夹在「真实模型」与「函数调用环」之间：模型响应先过它，再交给 MEA 决定调不调。
        // 只记录、不改语义——「工具找不到」的回填由 MEA 自己做（TerminateOnUnknownCalls 默认 false），
        // 这里补的是它做完之后没人留下痕迹的那一笔。
        ToolExecutionRun? run = null;
        var logger = _loggerFactory.CreateLogger<ToolExecutionPipeline>();
        var guard = new ToolCallGuardChatClient(innerClient, name =>
        {
            run?.MarkUnknownCall(name);
            logger.LogWarning("模型请求了不在本次工具清单里的工具：{Tool}", name);
        });

        var invoking = new FunctionInvokingChatClient(guard, _loggerFactory, _services)
        {
            MaximumIterationsPerRequest = limits.MaxIterations
        };

        run = new ToolExecutionRun(invoking);
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

            // 参数结构校验：按工具自己声明的形状，在执行体之前挡下缺必填 / 类型不符的调用。
            // 放在审批之前 —— 畸形参数不必去打扰审批者；挡下后把拒绝码作为结果回填给模型，
            // 让模型自己改参数或换路，与「被拒」「工具失败」在轨迹里分得开。
            var validationError = ValidateArguments(context.Function, context.Arguments);
            if (validationError is not null)
            {
                var payload = ValidationPayload(context.Function.Name, validationError);
                run.CompleteCall(index, Truncate(payload),
                    error: ToolValidationCodes.ArgumentValidationFailed, fullResult: payload);
                return payload;
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

                // 结果否决：先落轨迹、再判——拒答时那份命中仍然是证据，事后要能查"它看到了什么才拒的"。
                // 收口方式与守卫完全一致（Terminate 让这一轮不再回问模型），区别只在判定时机。
                var veto = limits.Veto?.Inspect(context.Function.Name, text);
                if (veto is not null)
                {
                    run.StopAt(veto);
                    context.Terminate = true;
                }

                // 进模型的结果与轨迹同源，按同一上界截断；事件仍带完整版（来源面板用）。
                // 只截轨迹会把全文留在模型上下文里，上界就白设了。
                return Truncate(text);
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

    /// <summary>
    /// 执行前按 <c>AIFunction.JsonSchema</c> 校验参数，只判 <c>required</c> 与 <c>type</c> 两个字段。
    /// 这不是一套 JSON Schema 引擎 —— enum / pattern / 嵌套 / 组合类型仍交给 MEA 的参数绑定，
    /// 绑定失败按工具异常归类；这里只把最常见的畸形参数在调用前挡下并给出可归因拒绝码。
    /// </summary>
    /// <returns>null = 结构可放行；非 null = 拒绝原因。</returns>
    private static string? ValidateArguments(AIFunction function, AIFunctionArguments arguments)
    {
        var schema = function.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
            return null; // 没有可判的结构，交给框架绑定

        if (schema.TryGetProperty("required", out var required) &&
            required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    continue;
                var name = item.GetString()!;
                if (!arguments.ContainsKey(name))
                    return $"缺少必填参数 {name}";
            }
        }

        foreach (var property in properties.EnumerateObject())
        {
            // 无 type / 联合类型（type 为数组）：不擅自判，交给绑定。
            if (!property.Value.TryGetProperty("type", out var typeDecl) ||
                typeDecl.ValueKind != JsonValueKind.String)
                continue;

            if (!arguments.TryGetValue(property.Name, out var value))
                continue; // 非必填且未提供

            if (!MatchesDeclaredType(typeDecl.GetString(), value))
                return $"参数 {property.Name} 应为 {typeDecl.GetString()}，实际为 {Describe(value)}";
        }

        return null;
    }

    private static bool MatchesDeclaredType(string? type, object? value)
    {
        if (value is JsonElement element)
        {
            return type switch
            {
                "string" => element.ValueKind == JsonValueKind.String,
                "integer" => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
                "number" => element.ValueKind == JsonValueKind.Number,
                "boolean" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "array" => element.ValueKind == JsonValueKind.Array,
                "object" => element.ValueKind == JsonValueKind.Object,
                "null" => element.ValueKind == JsonValueKind.Null,
                _ => true
            };
        }

        if (value is null)
            return type is "null";

        return type switch
        {
            "string" => value is string,
            "integer" => value is byte or sbyte or short or ushort or int or uint or long or ulong,
            "number" => value is byte or sbyte or short or ushort or int or uint or long or ulong
                or float or double or decimal,
            "boolean" => value is bool,
            "array" => value is System.Collections.IEnumerable && value is not string,
            "object" => value is System.Collections.IDictionary,
            "null" => false,
            _ => true
        };
    }

    private static string Describe(object? value)
        => value switch
        {
            null => "null",
            JsonElement element => element.ValueKind.ToString(),
            _ => value.GetType().Name
        };

    private static string ValidationPayload(string toolName, string reason)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["invalid_arguments"] = "true",
            ["tool"] = toolName,
            ["reason"] = ToolValidationCodes.ArgumentValidationFailed,
            ["detail"] = reason
        });

    /// <summary>序列化工具参数，并按参数名排序 —— 键序稳定，重复调用的指纹才可比。</summary>
    private static string Serialize(AIFunctionArguments arguments)
        => JsonSerializer.Serialize(arguments
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => ArgValue(kv.Value)));

    /// <summary>轨迹参数值：JsonElement 按其 JSON 值写入，标量保持 ToString()（字符串不带引号）。</summary>
    private static object? ArgValue(object? value) => value switch
    {
        JsonElement element => element.Clone(),
        _ => value?.ToString()
    };

    private string? Truncate(string? value)
        => value is null || value.Length <= _options.MaxResultChars
            ? value
            : value[.._options.MaxResultChars];
}
