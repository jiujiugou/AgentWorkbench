using System.Text.Json;
using AgentWorkbench.Tools.Builtin;

namespace AgentWorkbench.Webapi.Actions;

/// <summary>
/// Host 侧的输入身份守卫。<b>web 传输模型带来的必答题，只在本模块内解决，不进 Core。</b>
///
/// 背景：HTTP / SignalR 是「调用」不是「事件」，超时重试会让同一次意图送达两次。
/// 底座的判重表只认 requestId，所以「哪两次算同一次输入」必须由发起方自报，
/// 且服务端必须在最前面拒绝没有自报的调用（fail-closed），不能自己现编一个号。
///
/// 关键约束：号必须在<b>重试之前</b>就存在。服务端收到请求才生成对重试无效——
/// 那只会给重发那次再编一个新号，判重表仍然认不出。
///
/// 边界：Core 里 <c>ActionRequestId.Derive</c> 的「缺省回退随机号」保留不动。
/// 本模块保证从三条 web 入口进入的请求号都齐全，那条回退因此不可达。
/// 绕过 Host 直接调 Core（现存一处：Workflow 的 <c>AgentStepExecutor</c> 自己起 harness run）
/// 仍会触发回退，这是把解决方案留在 Host 的已知代价，见 <c>README.md</c>「输入身份契约」。
/// </summary>
public static class InputIdentity
{
    /// <summary>入口拒绝时返回给调用方的归类码。</summary>
    public const string MissingCode = "client_message_id_required";

    /// <summary>三条入口共用的判空规则；空白串也算缺。</summary>
    public static bool IsPresent(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Host 侧唯一算号入口：复用 Core 的派生规则，但先挡住「没有身份」这个输入。
    /// <paramref name="scope"/> 是隔离维度——工具环用真实会话 id，HTTP 递单入口用归属 id。
    /// </summary>
    public static string Derive(string scope, string clientMessageId, string actionKey, JsonElement arguments)
    {
        if (!IsPresent(clientMessageId))
            throw new ArgumentException(MissingCode, nameof(clientMessageId));

        return ActionRequestId.Derive(scope, clientMessageId, actionKey, arguments);
    }
}
