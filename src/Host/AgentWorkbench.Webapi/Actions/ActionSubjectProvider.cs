using System.Security.Claims;
using AgentWorkbench.Ports;
using Microsoft.AspNetCore.Http;

namespace AgentWorkbench.Webapi.Actions;

/// <summary>
/// 把当前已认证主体放进一次调用的作用域。宿主内部接口，刻意不进 Ports：
/// 「怎么把身份放进作用域」是宿主传输层的事，底座只需要一个只读端口。
/// </summary>
public interface IActionSubjectScope
{
    IDisposable Begin(string? subject);
}

/// <summary>
/// <see cref="IActionSubjectProvider"/> 的宿主实现。
///
/// 两条入口共用同一来源：
/// - HTTP 控制器：回退到 <see cref="IHttpContextAccessor"/> 当前请求的认证名；
/// - SignalR Hub：长连接的后续调用不保证还有可用的 HttpContext，
///   因此由 Hub 在整段生成期间 <c>Begin</c> 一个作用域，值随异步流进入工具调用。
///
/// 用 AsyncLocal 而不是单例字段：并发请求之间不串号，作用域结束自动还原。
/// 取不到身份时返回 null，由调用方按 fail-closed 拒绝——绝不回退到默认账号。
/// </summary>
public sealed class ActionSubjectProvider : IActionSubjectProvider, IActionSubjectScope
{
    private static readonly AsyncLocal<string?> ScopedSubject = new();

    private readonly IHttpContextAccessor _httpContext;

    public ActionSubjectProvider(IHttpContextAccessor httpContext) => _httpContext = httpContext;

    public string? CurrentSubject
    {
        get
        {
            var scoped = ScopedSubject.Value;
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

    public IDisposable Begin(string? subject) => new SubjectScope(subject);

    private sealed class SubjectScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public SubjectScope(string? subject)
        {
            _previous = ScopedSubject.Value;
            ScopedSubject.Value = subject;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            ScopedSubject.Value = _previous;
        }
    }
}
