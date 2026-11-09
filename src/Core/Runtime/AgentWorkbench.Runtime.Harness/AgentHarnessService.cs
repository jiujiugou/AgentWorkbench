using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using AgentWorkbench.Runtime.Context;
using AgentWorkbench.Runtime.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentWorkbench.Runtime.Harness;

/// <summary>
/// Agent 运行时门面：一次 run 的编排入口（loop / context / memory / tools / trace 各由独立模块提供）。
/// 本接口只做「装配 + 收尾 + 归类」，不实现任何单项能力。
/// </summary>
public interface IAgentHarness
{
    /// <summary>探测当前部署具备哪些能力（是否配了模型、是否启用上下文/记忆、可用工具名），供前端与探活使用。</summary>
    Task<HarnessCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行一次完整的 Agent run：解析模型 → 取/建会话 → 跑工具环 → 落轨迹 → 写回会话。
    /// 无论成功、失败、取消还是超时，都会留下一条执行轨迹后再向上抛出（取消/失败等）。
    /// </summary>
    Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式执行一次 Agent run：与 <see cref="RunAsync"/> 同一 loop、同一 guard、同一轨迹落库、同一会话写回，
    /// 差别只在产出形态——文本增量与工具完成事件实时推送（供 SignalR 等实时通道桥接），
    /// 终态只有一个 <see cref="HarnessStreamEventKind.Completed"/> 或 <see cref="HarnessStreamEventKind.Failed"/> 事件。
    /// </summary>
    IAsyncEnumerable<HarnessStreamEvent> StreamAsync(HarnessRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>按 runId 回放历史轨迹；未找到返回 <c>null</c>。</summary>
    Task<HarnessRunResult?> GetRunAsync(long runId, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class AgentHarnessService : IAgentHarness
{
    /// <summary>最终答复写入轨迹时的截断长度：轨迹用于回放/排障，不做全文归档，避免单条记录过大。</summary>
    private const int MaxAssistantChars = 400;

    /// <summary>统一采样温度：越低越确定，便于测试与结果复现。</summary>
    private const float Temperature = 0.7f;

    /// <summary>会话被占用时回给用户的文案。前端据此提示「稍候」，与「服务出错」区分开。</summary>
    private const string SessionBusyMessage = "这条会话正在回答中，请稍候再发。";

    // —— 以下依赖全部来自其它模块的契约，Harness 只负责把它们串起来 ——
    private readonly IChatClientFactory _factory;   // 模型网关：解析端点 / 产出 IChatClient
    private readonly IModelRuntime _runtime;        // 单次模型调用语义：超时 / 重试 / 并发闸门 / 用量
    private readonly IToolCatalog _tools;           // 工具目录：按白名单裁剪后的可用工具
    private readonly IToolExecutionPipeline _pipeline; // 工具执行环：把工具调用接进模型循环
    private readonly IContextAssembler _context; // 上下文组装：历史 / 指令 / 片段 → 模型请求
    private readonly ISessionStore _sessions;       // 会话存储：跨 run 的会话快照
    private readonly ISessionRunGate _gate;         // 会话互斥：同一 sessionId 同时只允许一个 run
    private readonly IOptions<MemoryOptions> _memoryOptions;   // 记忆开关（会话是否启用）
    private readonly IMemoryScopeAccessor _memoryScope;        // 记忆作用域：隔离并发 run 的 remember/recall
    private readonly IAgentLoop _loop;              // 执行控制：迭代预算、run 级超时、用量守卫
    private readonly IExecutionTraceStore _traces;  // 可观测：执行轨迹落库与回放
    private readonly ILoggerFactory _loggerFactory; // 传给 ChatClientAgent 构造日志
    private readonly IServiceProvider _services;    // 传给 ChatClientAgent 以解析其内部依赖
    private readonly ILogger<AgentHarnessService> _logger;

    public AgentHarnessService(
        IChatClientFactory factory,
        IModelRuntime runtime,
        IToolCatalog tools,
        IToolExecutionPipeline pipeline,
        IContextAssembler context,
        ISessionStore sessions,
        ISessionRunGate gate,
        IOptions<MemoryOptions> memoryOptions,
        IMemoryScopeAccessor memoryScope,
        IAgentLoop loop,
        IExecutionTraceStore traces,
        ILoggerFactory loggerFactory,
        IServiceProvider services,
        ILogger<AgentHarnessService> logger)
    {
        _factory = factory;
        _runtime = runtime;
        _tools = tools;
        _pipeline = pipeline;
        _context = context;
        _sessions = sessions;
        _gate = gate;
        _memoryOptions = memoryOptions;
        _memoryScope = memoryScope;
        _loop = loop;
        _traces = traces;
        _loggerFactory = loggerFactory;
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HarnessCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        // 能否跑起来完全取决于「有没有可解析的模型端点」，其余能力位只是配置开关的实际取值。
        var llm = await _factory.ResolveAsync(null, cancellationToken) is not null;
        return new HarnessCapabilities(
            Loop: true,
            Llm: llm,
            Context: _context.Options.Enabled,
            Memory: _memoryOptions.Value.SessionEnabled,
            Tools: _tools.GetToolNames());
    }

    /// <inheritdoc />
    public async Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken cancellationToken = default)
    {
        var sessionId = ResolveSessionId(request.SessionId);

        // 会话互斥：同一 sessionId 同时只允许一个 run 在推进。抢不到就如实回 session_busy，
        // 不排队（排队没有上限，第二句话可能几分钟后才处理，用户早已忘了上下文）。
        // 本次没有开始跑，因此不落轨迹——RunId=0 即「这次不算一次 run」。
        await using var gate = await _gate.TryAcquireAsync(sessionId, cancellationToken);
        if (gate is null)
            return SessionBusyResult(sessionId);

        using var memoryScope = _memoryScope.Begin(MemoryScope.Session(sessionId));
        var run = await PrepareRunAsync(request, sessionId, cancellationToken);

        try
        {
            // 执行控制：run 级超时由 AgentLoop 施加（超时抛 AgentRunTimeoutException）
            var response = await _loop.ExecuteAsync(
                async token => await run.Agent.RunAsync(
                    run.Messages, run.Session,
                    new ChatClientAgentRunOptions(new ChatOptions { Temperature = Temperature }),
                    token),
                cancellationToken);

            return await FinishSucceededAsync(run, response.Text ?? string.Empty, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消不是失败：它是「按用户意愿停下」，必须与 failed 分开，否则前端会把它当成故障上报。
            run.Stopwatch.Stop();
            await PersistQuietlyAsync(
                run.SessionId, AgentRunStatus.Cancelled, AgentRunCode.Cancelled, "调用方取消", run.Endpoint.Model,
                run.Stopwatch.ElapsedMilliseconds, run.Execution.Traces);
            throw;
        }
        catch (AgentRunTimeoutException ex)
        {
            // run 级墙钟预算（AgentLoop 施加）先于单次请求超时被识别，两者归类码不同。
            run.Stopwatch.Stop();
            await PersistQuietlyAsync(
                run.SessionId, AgentRunStatus.Timeout, AgentRunCode.RunTimeout, ex.Message, run.Endpoint.Model,
                run.Stopwatch.ElapsedMilliseconds, run.Execution.Traces);
            throw;
        }
        catch (TimeoutException ex)
        {
            run.Stopwatch.Stop();
            await PersistQuietlyAsync(
                run.SessionId, AgentRunStatus.Timeout, AgentRunCode.ModelTimeout, ex.Message, run.Endpoint.Model,
                run.Stopwatch.ElapsedMilliseconds, run.Execution.Traces);
            throw;
        }
        catch (Exception ex)
        {
            run.Stopwatch.Stop();
            await PersistQuietlyAsync(
                run.SessionId, AgentRunStatus.Failed, ClassifyFailure(ex, run.Execution), ex.Message, run.Endpoint.Model,
                run.Stopwatch.ElapsedMilliseconds, run.Execution.Traces);
            throw;
        }
        finally
        {
            // 会话写回：成功 / 失败 / 取消 / 超时都要落，否则重启后只有成功那轮记得住。
            if (_memoryOptions.Value.SessionEnabled)
                await SaveSessionQuietlyAsync(run.SessionId, run.SessionFactory, run.Session);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<HarnessStreamEvent> StreamAsync(
        HarnessRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sessionId = ResolveSessionId(request.SessionId);

        // 与 RunAsync 同一道闸：同一 sessionId 同时只允许一个 run。
        // 抢不到时产出一个 Failed 事件就结束——不抛异常，否则会被 Hub 的 catch 当成「服务出错」，
        // 与「这条会话正在回答」混在一起。
        await using var gate = await _gate.TryAcquireAsync(sessionId, cancellationToken);
        if (gate is null)
        {
            yield return new HarnessStreamEvent(
                HarnessStreamEventKind.Failed,
                Error: SessionBusyMessage,
                Status: AgentRunStatus.Failed,
                Code: AgentRunCode.SessionBusy);
            yield break;
        }

        using var memoryScope = _memoryScope.Begin(MemoryScope.Session(sessionId));
        var run = await PrepareRunAsync(request, sessionId, cancellationToken);

        // 工具完成 → 事件：管道在函数调用环内同步触发，这里只做非阻塞写入，不拖住模型循环。
        var channel = Channel.CreateUnbounded<HarnessStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        void OnToolCompleted(ToolCallTrace trace)
            => channel.Writer.TryWrite(new HarnessStreamEvent(HarnessStreamEventKind.ToolCompleted, Tool: trace));
        run.Execution.ToolCompleted += OnToolCompleted;

        // 生产者单独跑：它吞掉全部异常并转成 Failed 事件 + 完成通道，消费者侧永不因生产侧故障而中断读取。
        var producer = StreamRunAsync(channel.Writer, run, cancellationToken);
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
                yield return evt;
        }
        finally
        {
            run.Execution.ToolCompleted -= OnToolCompleted;
            // 消费者提前退出（调用方 break / 取消）时也要等生产者收尾——它负责落轨迹与写回会话。
            await producer;
        }
    }

    /// <summary>
    /// 流式 run 的生产者：跑模型循环、把文本增量与工具完成事件写进通道，收尾落轨迹 + 写回会话。
    /// 结构与 <see cref="RunAsync"/> 一一对应（同一 loop / guard / trace），异常全部转成 <see cref="HarnessStreamEventKind.Failed"/>。
    /// </summary>
    private async Task StreamRunAsync(
        ChannelWriter<HarnessStreamEvent> writer, PreparedRun run, CancellationToken cancellationToken)
    {
        var answer = new StringBuilder();
        try
        {
            // run 级超时同样施加：把整段流式 drain 包进 AgentLoop 的墙钟预算（超时抛 AgentRunTimeoutException）。
            // drain 的返回值即累积全文，随后交给 FinishSucceededAsync 落轨迹——两条出口的收尾完全同路。
            var streamed = await _loop.ExecuteAsync(
                async token =>
                {
                    await foreach (var update in run.Agent.RunStreamingAsync(
                        run.Messages, run.Session,
                        new ChatClientAgentRunOptions(new ChatOptions { Temperature = Temperature }),
                        token))
                    {
                        if (string.IsNullOrEmpty(update.Text))
                            continue;
                        answer.Append(update.Text);
                        writer.TryWrite(new HarnessStreamEvent(HarnessStreamEventKind.TextDelta, Text: update.Text));
                    }

                    return answer.ToString();
                },
                cancellationToken);

            run.Stopwatch.Stop();
            var result = await FinishSucceededAsync(run, streamed, cancellationToken);
            writer.TryWrite(new HarnessStreamEvent(HarnessStreamEventKind.Completed, Result: result));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            run.Stopwatch.Stop();
            await PersistQuietlyAsync(
                run.SessionId, AgentRunStatus.Cancelled, AgentRunCode.Cancelled, "调用方取消", run.Endpoint.Model,
                run.Stopwatch.ElapsedMilliseconds, run.Execution.Traces);
            writer.TryWrite(new HarnessStreamEvent(HarnessStreamEventKind.Failed, Error: "已停止生成。", Status: AgentRunStatus.Cancelled));
        }
        catch (AgentRunTimeoutException ex)
        {
            run.Stopwatch.Stop();
            await PersistQuietlyAsync(
                run.SessionId, AgentRunStatus.Timeout, AgentRunCode.RunTimeout, ex.Message, run.Endpoint.Model,
                run.Stopwatch.ElapsedMilliseconds, run.Execution.Traces);
            writer.TryWrite(new HarnessStreamEvent(HarnessStreamEventKind.Failed, Error: ex.Message, Status: AgentRunStatus.Timeout));
        }
        catch (TimeoutException ex)
        {
            run.Stopwatch.Stop();
            await PersistQuietlyAsync(
                run.SessionId, AgentRunStatus.Timeout, AgentRunCode.ModelTimeout, ex.Message, run.Endpoint.Model,
                run.Stopwatch.ElapsedMilliseconds, run.Execution.Traces);
            writer.TryWrite(new HarnessStreamEvent(HarnessStreamEventKind.Failed, Error: ex.Message, Status: AgentRunStatus.Timeout));
        }
        catch (Exception ex)
        {
            run.Stopwatch.Stop();
            await PersistQuietlyAsync(
                run.SessionId, AgentRunStatus.Failed, ClassifyFailure(ex, run.Execution), ex.Message, run.Endpoint.Model,
                run.Stopwatch.ElapsedMilliseconds, run.Execution.Traces);
            writer.TryWrite(new HarnessStreamEvent(HarnessStreamEventKind.Failed, Error: ex.Message, Status: AgentRunStatus.Failed));
        }
        finally
        {
            if (_memoryOptions.Value.SessionEnabled)
                await SaveSessionQuietlyAsync(run.SessionId, run.SessionFactory, run.Session);
            writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public async Task<HarnessRunResult?> GetRunAsync(long runId, CancellationToken cancellationToken = default)
    {
        var trace = await _traces.GetAsync(runId, cancellationToken);
        if (trace is null)
            return null;
        // 从轨迹还原：最终答复取最后一条 assistant 步（已截断），其余字段直接映射。
        return new HarnessRunResult(
            runId, trace.SessionId,
            trace.Steps.LastOrDefault(s => s.Kind == "assistant")?.Result ?? string.Empty,
            trace.Iterations, trace.Model, trace.LatencyMs,
            trace.Steps.Select(s => new HarnessStep(s.Iteration, s.Kind, s.Tool, s.Arguments, s.Result, s.Error)).ToList(),
            trace.Status, trace.ErrorCode);
    }

    /// <summary>缺省 sessionId 时随机生成，保证「无状态调用」也能各 run 隔离；显式传入则复用同一会话。</summary>
    private static string ResolveSessionId(string? sessionId)
        => string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId!;

    /// <summary>
    /// 被会话闸拒绝的结果。<c>RunId=0</c> 表示本次没有开始跑、也没有落轨迹——
    /// 不是「跑了一次但失败了」，回放接口拿 0 查不到东西是预期行为。
    /// </summary>
    private static HarnessRunResult SessionBusyResult(string sessionId)
        => new(0, sessionId, string.Empty, 0, null, 0, [], AgentRunStatus.Failed, AgentRunCode.SessionBusy);

    /// <summary>
    /// 一次 run 的公共准备（<see cref="RunAsync"/> 与 <see cref="StreamAsync"/> 共用）：
    /// 解析模型 → 装配工具环与守卫 → 建 Agent → 开/建会话。模型未配置时已落轨迹并抛出。
    /// </summary>
    private async Task<PreparedRun> PrepareRunAsync(
        HarnessRunRequest request, string sessionId, CancellationToken cancellationToken)
    {
        // 墙钟计时覆盖整条 run（含检索 / 工具 / 模型），最终写入轨迹的 LatencyMs 即取自这里。
        var sw = Stopwatch.StartNew();

        ChatEndpoint endpoint;
        try
        {
            endpoint = await _factory.ResolveAsync(null, cancellationToken)
                ?? throw new HarnessModelNotConfiguredException();
        }
        catch (HarnessModelNotConfiguredException ex)
        {
            // 起不来也要留痕：调用方按 error_code=model_unavailable 就能区分「没配模型」和「模型跑了但失败」。
            sw.Stop();
            await PersistQuietlyAsync(
                sessionId, AgentRunStatus.Failed, AgentRunCode.ModelUnavailable, ex.Message,
                model: null, sw.ElapsedMilliseconds, traces: []);
            throw;
        }

        // 模型请求统一走 ModelRuntime（超时 / 重试 / 用量），工具环预算来自 AgentLoop，工具授权已由 ToolCatalog 按白名单裁剪
        var chatClient = _runtime.Wrap(_factory.GetClient(endpoint));

        // 执行控制守卫：一次 run 一个实例，管住「卡在同一个调用上」与「把 token 烧光」；
        // 判定在工具环内进行（见 ToolExecutionPipeline），这里只负责把它装起来。
        var guard = _loop.CreateGuard(new RuntimeUsageSource(_runtime));
        // 审批级别由 IToolApprovalPolicy 决定（ToolExecution 侧消费）；这里只把「问谁」透传下去。
        // 两者是正交的两维：白名单/审批级别来自配置模块，approver 来自本次 run 的调用方。
        // 结果否决由 DI 提供（领域模块注册），编排层把它透传进本次 run 的限额——
        // 编排层不认识否决的具体内容，只知道"有人可能在工具返回后要求停"。
        var execution = _pipeline.Create(
            chatClient,
            new ToolExecutionLimits(
                _loop.MaxIterations, guard, request.Approver, _services.GetService<IToolResultVeto>()));
        // 上下文组装统一走 Context 模块：消息形态、历史截断、指令 / 片段预算都在那里。
        // 注入用 IChatClient 装饰器（Wrap），不挂 Agent 的 AIContextProviders ——
        // 后者会让 MEA 在工具环外面再包一层，绕过守卫与轨迹采集。
        var bundle = _context.Assemble(new ContextRequest(
            request.Input, Instructions: request.Instructions));
        var agentClient = _context.Wrap(execution.Client, bundle);
        // 用「带工具环的 client」构建 Agent：模型每轮的工具调用都会经过 execution 管线。
        var agent = BuildAgent(agentClient);

        // 用户原文直接进模型：知识检索不再由 harness 暗中做——需要依据时由模型主动调
        // search_knowledge 工具，调用留痕、可回放、可判分。曾经的 UseKnowledge 暗路与工具
        // 路径重复（同一能力两条路），且全仓零测试覆盖，已拆。
        var messages = bundle.Messages.ToList();

        // 会话启用走存储（可跨 run 续接历史），否则用进程内一次性会话（无记忆）。
        var sessionFactory = new AgentSessionFactory(agent);
        var session = _memoryOptions.Value.SessionEnabled
            ? await _sessions.GetOrCreateAsync(sessionId, sessionFactory, cancellationToken)
            : await agent.CreateSessionAsync(cancellationToken);

        // 历史 provider 靠这个键查库：MAF 的 AgentSession 本身不带 id，id 由编排层维护。
        // 落库版 provider 用它定位「这是哪个会话的历史」；内存版忽略它。
        // 写入时机必须在 BuildAgent 之后、本次 run 真正开始之前。
        session.StateBag.SetValue(AgentSessionKeys.SessionId, sessionId, null!);

        return new PreparedRun(sessionId, endpoint, execution, agent, session, sessionFactory, sw, guard, messages);
    }

    /// <summary>
    /// 成功收尾（两条出口共用）：分类提前终止 → 组装步骤 → 落轨迹 → 记日志 → 产出结果。
    /// </summary>
    private async Task<HarnessRunResult> FinishSucceededAsync(
        PreparedRun run, string assistantText, CancellationToken cancellationToken)
    {
        // 收尾分类：环内提前终止（重复调用 / 预算耗尽）优先；否则问守卫「是不是跑满迭代被强制收口」
        // —— 框架跑满迭代时不报错，而是清空工具逼模型用文本收口再正常返回，不主动问就与「答完了」无法区分。
        var stop = run.Execution.Stop ?? run.Guard.InspectCompletion();

        // 否决式收口（目前只有"依据不足"一种）：终止原因自带要给用户看的那句话。
        // 此时模型这条路已经断了——它一个字都没产出，答复只能从这里来，否则界面是一片空白。
        if (stop is not null && string.Equals(stop.Status, AgentRunStatus.Refused, StringComparison.Ordinal))
            assistantText = stop.Message;

        var steps = BuildSteps(run.Execution.Traces, assistantText);
        var iterations = steps.Count == 0 ? 0 : steps.Max(s => s.Iteration) + 1;
        var status = stop?.Status ?? AgentRunStatus.Succeeded;

        var runId = await _traces.SaveAsync(
            ExecutionTrace.Create(
                run.SessionId, status, stop?.Code, iterations, run.Endpoint.Model, run.Stopwatch.ElapsedMilliseconds,
                stop?.Message, ToTraceSteps(steps)),
            cancellationToken);

        _logger.LogInformation(
            "Harness run {RunId} session={SessionId} status={Status} code={Code} iterations={Iterations} tools={ToolSteps} failedToolSteps={FailedToolSteps} in {ElapsedMs}ms",
            runId, run.SessionId, status, stop?.Code, iterations,
            steps.Count(s => s.Kind == "tool"), steps.Count(s => s.Error is not null),
            run.Stopwatch.ElapsedMilliseconds);

        return new HarnessRunResult(
            runId, run.SessionId, assistantText, iterations, run.Endpoint.Model, run.Stopwatch.ElapsedMilliseconds,
            steps, status, stop?.Code);
    }

    /// <summary>
    /// 一次 run 的公共准备产物：装配结果 + 会话 + 计时 + 守卫，两条出口（同步 / 流式）都消费它。
    /// </summary>
    private sealed record PreparedRun(
        string SessionId,
        ChatEndpoint Endpoint,
        ToolExecutionRun Execution,
        ChatClientAgent Agent,
        AgentSession Session,
        AgentSessionFactory SessionFactory,
        Stopwatch Stopwatch,
        IAgentRunGuard Guard,
        List<ChatMessage> Messages);

    /// <summary>
    /// 用工具环 client 构建框架 Agent。工具列表来自 <see cref="IToolCatalog"/>（已按白名单裁剪）；
    /// 历史由内存 provider 托管，跨 run 持久化则由外层会话存储负责。
    /// </summary>
    /// <param name="client">已装配好的模型客户端（工具环 + 可选的指令装饰器）。</param>
    /// <remarks>
    /// <c>UseProvidedChatClientAsIs = true</c> 是硬要求：它让 MEA 原样使用我们这条链路，
    /// 而不是另包一层函数调用环（那会绕过 <c>ToolExecutionPipeline</c> 的守卫与轨迹采集）。
    /// 同理这里<b>不设置</b> <c>AIContextProviders</c>——指令与片段走
    /// <c>IContextAssembler.Wrap</c> 的装饰器（见 Runtime.Context 的 README）。
    /// </remarks>
    private ChatClientAgent BuildAgent(IChatClient client)
    {
        var options = new ChatClientAgentOptions
        {
            Name = "AgentHarness",
            UseProvidedChatClientAsIs = true,
            ChatHistoryProvider = _context.CreateChatHistoryProvider(),
            ChatOptions = new ChatOptions { Tools = [.. _tools.GetTools()], Temperature = Temperature }
        };
        return new ChatClientAgent(client, options, _loggerFactory, _services);
    }

    /// <summary>
    /// 失败 / 取消 / 超时路径落库：绝不因落库本身出错而掩盖原始异常，也绝不因调用方已取消而丢记录。
    /// </summary>
    private async Task PersistQuietlyAsync(
        string sessionId, string status, string? code, string error, string? model, long elapsedMs,
        IReadOnlyList<ToolCallTrace> traces)
    {
        try
        {
            await PersistRunAsync(
                sessionId, status, code, error, model, elapsedMs, traces, assistantText: null,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "记录 run 轨迹时出错 session={SessionId} status={Status}", sessionId, status);
        }
    }

    /// <summary>把一次 run 的轨迹（工具步 + 可选 assistant 步）组装并落库，返回新生成的 runId。</summary>
    private async Task<long> PersistRunAsync(
        string sessionId, string status, string? code, string? error, string? model, long elapsedMs,
        IReadOnlyList<ToolCallTrace> traces, string? assistantText, CancellationToken cancellationToken)
    {
        var steps = BuildSteps(traces, assistantText);
        var iterations = steps.Count == 0 ? 0 : steps.Max(s => s.Iteration) + 1;
        return await _traces.SaveAsync(
            ExecutionTrace.Create(
                sessionId, status, code, iterations, model, elapsedMs, error, ToTraceSteps(steps)),
            cancellationToken);
    }

    /// <summary>
    /// 把冒泡上来的异常归类成 <see cref="AgentRunCode"/>：工具执行环记录过的异常算工具异常，其余算未归类。
    /// </summary>
    private static string ClassifyFailure(Exception exception, ToolExecutionRun execution)
    {
        foreach (var candidate in Flatten(exception))
        {
            foreach (var failure in execution.ToolFailures)
            {
                // 实例引用是第一判据；框架（MEA）在「连续失败越限」时可能重抛或包一层，
                // 所以类型 + 消息相同的也认，避免归类退化成 unhandled_error。
                if (ReferenceEquals(candidate, failure) ||
                    (candidate.GetType() == failure.GetType() &&
                     string.Equals(candidate.Message, failure.Message, StringComparison.Ordinal)))
                    return AgentRunCode.ToolError;
            }
        }

        return AgentRunCode.Unhandled;
    }

    /// <summary>把异常树（含 <see cref="AggregateException"/>）深度优先展开，供逐层匹配工具失败记录。</summary>
    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        var stack = new Stack<Exception>();
        stack.Push(exception);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    stack.Push(inner);
            }
            else if (current.InnerException is { } innerException)
            {
                stack.Push(innerException);
            }
        }
    }

    /// <summary>
    /// 把工具调用轨迹映射为统一步骤列表，并在末尾追加最终答复步（iteration 取最后一步 +1）。
    /// </summary>
    private static IReadOnlyList<HarnessStep> BuildSteps(IReadOnlyList<ToolCallTrace> traces, string? assistantText)
    {
        var steps = traces
            .Select(t => new HarnessStep(t.Iteration, "tool", t.Name, t.Arguments, t.Result, t.Error))
            .ToList();

        // 空答复不落成步骤：提前终止（循环 / 预算）时模型往往没有最终文本，
        // 落一条空 assistant 步会把「没有答复」写成「答复为空」，回放时分不清。
        if (string.IsNullOrEmpty(assistantText))
            return steps;

        var finalIteration = steps.Select(s => s.Iteration).DefaultIfEmpty(-1).Max() + 1;
        steps.Add(new HarnessStep(finalIteration, "assistant", null, null, Truncate(assistantText)));
        return steps;
    }

    /// <summary>Harness 步骤 → 持久化步骤（<see cref="ExecutionStep"/>）的映射。</summary>
    private static IReadOnlyList<ExecutionStep> ToTraceSteps(IEnumerable<HarnessStep> steps)
        => steps.Select(s => new ExecutionStep(s.Iteration, s.Kind, s.Tool, s.Arguments, s.Result, s.Error)).ToList();

    /// <summary>按 <see cref="MaxAssistantChars"/> 截断文本；<c>null</c> 原样透传（与空串区分）。</summary>
    private static string? Truncate(string? value)
        => value is null ? null : value.Length <= MaxAssistantChars ? value : value[..MaxAssistantChars];

    /// <summary>会话写回 best-effort：写库失败不能掩盖本次 run 的结局，也绝不能因调用方已取消而丢写。</summary>
    private async Task SaveSessionQuietlyAsync(string sessionId, ISessionFactory factory, AgentSession session)
    {
        try
        {
            await _sessions.SaveAsync(sessionId, factory, session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存会话时出错 session={SessionId}", sessionId);
        }
    }
}
