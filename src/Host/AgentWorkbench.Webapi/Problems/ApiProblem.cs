using Microsoft.AspNetCore.Mvc;

namespace AgentWorkbench.Webapi.Problems;

/// <summary>
/// 统一错误载体：用 ASP.NET Core 的 ProblemDetailsFactory（RFC 9457）生成错误体，
/// 不再在控制器里就地 new 匿名对象。ADR 0018 §二 #5。
///
/// 本文件是唯一碰 ASP.NET 类型的地方：只负责把 <see cref="ApiError"/> 翻译成 HTTP 响应，
/// 不认识任何业务码、不拼异常文案、不判定什么算失败。业务判定留在控制器与各业务模块。
///
/// 跨进程约定的五字段（code / message / details / request_id / retryable）放进
/// Extensions，序列化后仍平铺在 JSON 顶层——前端与契约测试看到的形状不变。
/// request_id 单点取 HttpContext.TraceIdentifier；接入统一请求中间件后只改这一行，
/// 不要在控制器里另造一套。
/// </summary>
public static class ApiProblem
{
    private const string ProblemJsonMediaType = "application/problem+json";

    public static IActionResult Result(this ControllerBase controller, ApiError error)
    {
        var httpContext = controller.HttpContext;
        var problem = controller.ProblemDetailsFactory.CreateProblemDetails(
            httpContext,
            statusCode: error.Status,
            detail: error.Message);

        // 错误独有的额外字段先合并，五字段后写：五字段优先，Extra 不许劫持对外契约。
        if (error.Extra is not null)
            foreach (var pair in error.Extra)
                problem.Extensions[pair.Key] = pair.Value;

        // 框架工厂默认会塞一个 traceId；对外契约只留 request_id，避免一个响应里两个 id。
        problem.Extensions.Remove("traceId");

        problem.Extensions["code"] = error.Code;
        problem.Extensions["message"] = error.Message;
        problem.Extensions["details"] = error.Details;
        problem.Extensions["request_id"] = httpContext.TraceIdentifier;
        problem.Extensions["retryable"] = error.Retryable;

        var result = new ObjectResult(problem) { StatusCode = error.Status };
        // 裸 ObjectResult 协商出来是 application/json；显式声明才是 RFC 9457 的媒体类型。
        result.ContentTypes.Add(ProblemJsonMediaType);
        return result;
    }
}
