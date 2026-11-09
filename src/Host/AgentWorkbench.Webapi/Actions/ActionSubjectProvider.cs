using System.Security.Claims;
using AgentWorkbench.Ports;
using Microsoft.AspNetCore.Http;

namespace AgentWorkbench.Webapi.Actions;

/// <summary>
/// 把「谁在提」与「这是哪一次输入」放进一次调用的作用域。宿主内部接口，刻意不进 Ports：
/// 怎么把值放进作用域是宿主传输层的事，底座只需要两个只读端口。
/// </summary>
public interface IActionSubjectScope
{
    /// <param name="subject">当前已认证主体名。</param>
    /// <param name="conversationId">本次输入所属会话；用于派生请求号。</param>
    /// <param name="clientMessageId">本次输入的客户端消息标识；用于派生请求号。</param>
    IDisposable Begin(string? subject, string? conversationId = null, string? clientMessageId = null);
}

/// <summary>
/// <see cref="IActionSubjectProvider"/> 与 <see cref="IActionOriginProvider"/> 的宿主实现。
///
/// 两条入口共用同一来源：
/// - HTTP 控制器：身份回退到 <see cref="IHttpContextAccessor"/> 当前请求的认证名；
///   HTTP 入口本来就自带 requestId / idempotencyKey，不参与派生。
/// - SignalR Hub：长连接的后续调用不保证还有可用的 HttpContext，
///   因此由 Hub 在整段生成期间 <c>Begin</c> 一个作用域，值随异步流进入工具调用。
///
/// 用一个 AsyncLocal 记录三元组而不是三个单例字段：并发请求之间不串号，作用域结束自动还原。
/// 取不到身份时返回 null，由调用方按 fail-closed 拒绝——绝不回退到默认账号。
/// 取不到会话 / 消息标识时返回 null，请求号退化为一次性 ID——绝不退化成共用一个号。
/// </summary>
public sealed class ActionSubjectProvider : IActionSubjectProvider, IActionOriginProvider, IActionSubjectScope
{
    private static readonly AsyncLocal<ScopeState?> Scoped = new();

    private readonly IHttpContextAccessor _httpContext;

    public ActionSubjectProvider(IHttpContextAccessor httpContext) => _httpContext = httpContext;

    public string? CurrentSubject
    {
        get
        {
            var scoped = Scoped.Value?.Subject;
            if (!string.IsNullOrWhiteSpace(scoped))
                return scoped;

            var principal = _httpContext.HttpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
                return null;

            var name = principal.Identity.Name;
            return string.IsNullOrWhiteSpace(name)
                ? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                : name;
        }
    }

    /// <summary>
    /// 作用域里显式放入的会话标识。HTTP 入口不建作用域，因此为 null ——
    /// 那条路由客户端自带 requestId，不参与派生。
    /// </summary>
    public string? ConversationId => Scoped.Value?.ConversationId;

    /// <summary>作用域里显式放入的客户端消息标识；为空时工具侧回退为一次性请求号。</summary>
    public string? ClientMessageId => Scoped.Value?.ClientMessageId;

    public IDisposable Begin(string? subject, string? conversationId = null, string? clientMessageId = null)
        => new SubjectScope(new ScopeState(subject, conversationId, clientMessageId));

    private sealed record ScopeState(string? Subject, string? ConversationId, string? ClientMessageId);

    private sealed class SubjectScope : IDisposable
    {
        private readonly ScopeState? _previous;
        private bool _disposed;

        public SubjectScope(ScopeState current)
        {
            _previous = Scoped.Value;
            Scoped.Value = current;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Scoped.Value = _previous;
        }
    }
}
