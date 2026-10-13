using System.Text.Json;

namespace AgentWorkbench.ModelInvocation;

/// <summary>
/// 单步模型调用契约：给完整历史 + 可用工具，返回**一条** assistant 消息。
/// </summary>
/// <remarks>
/// 语义边界（τ²-bench 适配的核心）：
/// <list type="bullet">
/// <item>不执行工具——工具执行权在调用方（τ² 的 Python orchestrator）；</item>
/// <item>不循环——一次调用一次返回，多轮由调用方驱动；</item>
/// <item>不落库——不写对话/轨迹/记忆，纯函数式转发。</item>
/// </list>
/// 与 src 侧 <c>POST /api/harness/run</c>（run 级，内部含 AgentLoop 多轮循环）互补：
/// 那是产品面，这是评测适配面，两者不共用代码路径。
/// </remarks>
public interface IModelInvoker
{
    /// <summary>
    /// 执行一次单步模型调用。
    /// </summary>
    /// <param name="request">完整消息历史（含 tool 结果）+ 可用工具声明 + 可选模型覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>单条 assistant 消息 + 实际模型名 + 本次调用的近似用量增量。</returns>
    /// <exception cref="InvalidOperationException">模型未配置（数据库无激活供应商且配置回退未配置）。</exception>
    Task<InvokeResponse> InvokeAsync(InvokeRequest request, CancellationToken cancellationToken);
}

/// <summary>请求：完整历史 + 工具声明 + 可选模型覆盖。</summary>
/// <param name="Messages">完整消息历史，顺序为时间序；最后一条通常是 user 或 tool。</param>
/// <param name="Tools">可用工具声明（JSON Schema 形式，无委托绑定——执行权不在本服务）。可为 null/空。</param>
/// <param name="Model">模型名覆盖；null/空白时按「数据库激活供应商默认模型 → 配置回退」解析。</param>
public sealed record InvokeRequest(
    IReadOnlyList<InvokeMessage> Messages,
    IReadOnlyList<InvokeTool>? Tools,
    string? Model);

/// <summary>响应：单条 assistant 消息 + 实际模型 + 本次近似用量增量。</summary>
/// <param name="Message">单条 assistant 消息：要么是文本，要么带 ToolCalls（也可能两者并存，按模型实际行为如实返回）。</param>
/// <param name="Model">本次实际使用的模型名（由端点解析结果决定，回显便于调用方记账）。</param>
/// <param name="Usage">本次调用的近似用量增量（见 <see cref="InvokeUsage"/> 的并发说明）。</param>
public sealed record InvokeResponse(
    InvokeMessage Message,
    string Model,
    InvokeUsage Usage);

/// <summary>
/// 一条消息（协议 DTO，与 Microsoft.Extensions.AI 的内部类型解耦——
/// 对外契约稳定，不随框架版本漂移；Python 侧按这个形状解析）。
/// </summary>
/// <param name="Role">user / assistant / tool / system。</param>
/// <param name="Text">文本内容；tool 消息为工具执行结果文本，assistant 消息可为 null（纯工具调用时）。</param>
/// <param name="ToolCalls">assistant 消息携带的工具调用；文本消息为 null/空。</param>
/// <param name="ToolCallId">tool 消息必填：被回结果的那次工具调用 Id。</param>
public sealed record InvokeMessage(
    string Role,
    string? Text,
    IReadOnlyList<InvokeToolCall>? ToolCalls,
    string? ToolCallId);

/// <summary>一次工具调用。</summary>
/// <param name="Id">调用 Id（tool 消息通过它回结果）。</param>
/// <param name="Name">工具名。</param>
/// <param name="Arguments">参数 JSON 字符串（原样透传，不做解析）。</param>
public sealed record InvokeToolCall(string Id, string Name, string Arguments);

/// <summary>工具声明（仅 schema，无执行体）。</summary>
/// <param name="Name">工具名。</param>
/// <param name="Description">工具描述（给模型读的）。</param>
/// <param name="Parameters">参数 JSON Schema；null 表示无参。</param>
public sealed record InvokeTool(string Name, string? Description, JsonElement? Parameters);

/// <summary>
/// 本次调用的近似用量增量：调用前后两次读取进程累计计量器的差值。
/// </summary>
/// <remarks>
/// 并发说明：进程级计量器是共享的，差值包含并发期间其他请求的增量，
/// 因此这是「近似值」而非精确值。用途是评测侧粗略记账（量级判断），
/// 不做计费、不做门禁。要精确到每次调用，需要调用方串行化或改造计量器为请求级。
/// </remarks>
/// <param name="Requests">请求数（重试的每次尝试各计一次，可能大于 1）。</param>
/// <param name="InputTokens">输入 token 增量。</param>
/// <param name="OutputTokens">输出 token 增量。</param>
public sealed record InvokeUsage(long Requests, long InputTokens, long OutputTokens);
