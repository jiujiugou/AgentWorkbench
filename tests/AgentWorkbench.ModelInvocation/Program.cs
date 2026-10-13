using AgentWorkbench.ModelInvocation;
using AgentWorkbench.ModelRuntime;
using AgentWorkbench.Persistence;
using AgentWorkbench.Providers;

// AgentWorkbench.ModelInvocation: τ²-bench 适配面——单步模型调用服务。
// 给 messages[] + tools[]，返回单条 assistant 消息（文本或 tool_calls）；
// 不执行工具、不循环、不落库。工具执行权与判分权都在 τ² 的 Python orchestrator 侧。
//
// Endpoints
//   POST /api/model/invoke   单步调用（协议 DTO 见 IModelInvoker.cs）
//   GET  /health             健康检查
//
// 为什么在 tests/ 下（而不是 src/AgentWorkbench.ModelInvocation）：
//   见 csproj 注释——无 src 消费者、架构测试看不到本目录、Webapi 不长评测端点。
//
// 模型配置两种接法（env 覆盖，V10 已证生效）：
//   1) Provider__BaseUrl / Provider__ApiKey / Provider__Model  直配（推荐：评测隔离，
//      默认库为空库、无激活供应商，自动降级到此配置节）
//   2) Storage__DbPath=<已迁移的库路径>                         复用数据库里的激活供应商
// 存储路径相对 ContentRoot 解析（StoragePaths.Resolve），与进程工作目录无关。
// 下面启动时只做迁移（FluentMigrator 幂等，与 Webapi 同时迁移安全），不种子供应商
// ——「库为空 + 无配置」时解析结果为 null，端点返回 503 model_unavailable，是可判定状态。
//
// 端口：默认 http://localhost:5000（可用 ASPNETCORE_URLS 覆盖）。

var builder = WebApplication.CreateBuilder(args);

// 只装配本面需要的四个模块：存储（读激活供应商）+ 供应商（端点解析/客户端缓存）
// + 模型运行时（超时/重试/闸门/用量）+ 单步调用实现。
builder.Services.AddNitroPersistence(builder.Configuration, builder.Environment);
builder.Services.AddNitroProviders(builder.Configuration);
builder.Services.AddNitroModelRuntime(builder.Configuration);
builder.Services.AddNitroModelInvocation(builder.Configuration);

var app = builder.Build();

// 启动即迁移（幂等；不种子——种子是 Webapi 组合根的职责，评测服务保持「空库 + 配置兜底」语义）。
app.InitializeDatabase();

app.MapGet("/health", () => Results.Json(new { status = "ok", role = "model-invocation" }));

app.MapPost("/api/model/invoke", async (
    HttpContext ctx,
    IModelInvoker invoker,
    CancellationToken ct) =>
{
    InvokeRequest? request;
    try
    {
        request = await ctx.Request.ReadFromJsonAsync<InvokeRequest>(ContractJson.Options, ctx.RequestAborted);
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.BadRequest(new { error = "invalid_request", message = "请求体不是合法 JSON 或不符合 InvokeRequest 契约" });
    }

    if (request is null || request.Messages.Count == 0)
        return Results.BadRequest(new { error = "invalid_request", message = "messages 不能为空" });

    try
    {
        var response = await invoker.InvokeAsync(request, ct);
        return Results.Json(response, ContractJson.Options);
    }
    catch (ArgumentException ex)
    {
        // 调用方输入错误（未知角色 / tool 消息缺 toolCallId）：400，不是服务端崩溃。
        return Results.BadRequest(new { error = "invalid_request", message = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        // 模型未配置 / 模型未回 assistant 消息：调用方可判定的服务端状态，不是崩溃。
        return Results.Json(
            new { error = "model_unavailable", message = ex.Message },
            statusCode: 503);
    }
});

app.Run();

// 顶级语句之后的成员声明区。字段不能直接挂在顶级语句程序中（CS0106），
// 必须裹进类型——照 MockLlm 的先例。
internal static class ContractJson
{
    // 入向大小写不敏感（Python 侧习惯 camelCase/snake_case 混用）；
    // 出向 camelCase——τ² 适配器是 Python 消费者，README 的响应示例即真实输出形状。
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };
}
