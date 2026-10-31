using AgentWorkbench.AgentLoop;
using AgentWorkbench.AgentHarness;
using AgentWorkbench.Conversation;
using AgentWorkbench.Documents;
using AgentWorkbench.Knowledge.Local;
using AgentWorkbench.Memory;
using AgentWorkbench.Memory.Persistence;
using AgentWorkbench.ModelRuntime;
using AgentWorkbench.Observability;
using AgentWorkbench.Persistence;
using AgentWorkbench.Providers;
using AgentWorkbench.ToolExecution;
using AgentWorkbench.Workflow;
using AgentWorkbench.Webapi.Auth;
using AgentWorkbench.Webapi.Hubs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/agentworkbench-.log", rollingInterval: RollingInterval.Day));

// 上传体积上限
const long maxUploadBytes = 64L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxUploadBytes);

// ── 模块注册（新增能力 = 新增一行 AddNitroXxx）──
builder.Services.AddNitroPersistence(builder.Configuration, builder.Environment);
builder.Services.AddNitroProviders(builder.Configuration);
builder.Services.AddNitroModelRuntime(builder.Configuration);
builder.Services.AddNitroToolExecution(builder.Configuration);
builder.Services.AddNitroConversation(builder.Configuration);
builder.Services.AddNitroKnowledge(builder.Configuration);
builder.Services.AddNitroDocuments(builder.Configuration);
builder.Services.AddNitroMemory(builder.Configuration);
builder.Services.AddNitroMemoryPersistence(builder.Configuration);
builder.Services.AddNitroObservability();
builder.Services.AddNitroAgentLoop(builder.Configuration);
builder.Services.AddNitroAgentHarness(builder.Configuration);
builder.Services.AddNitroWorkflow(builder.Configuration);
// 用 SignalR 覆盖 Workflow 模块的默认空进度通知
builder.Services.AddSingleton<IWorkflowProgressNotifier, SignalRWorkflowProgressNotifier>();

// MaximumParallelInvocationsPerClient 默认 1：同一连接上的 Hub 方法是串行的。
// 串行意味着「生成中」时 Stop() 排在 Send() 后面永远轮不到——停止按钮成了摆设
// （实测：Stop 在 2s 发出，run 仍跑满 4.4s 才结束）。放宽到 4 让 Stop 与 Send 并行。
builder.Services.AddSignalR(o => o.MaximumParallelInvocationsPerClient = 4);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── 身份（多租户的「谁」）──
// Cookie 而非 Bearer：前端与后端同源（vite 代理 /api 与 /hubs），cookie 天然随请求走，SignalR 握手也带得上。
// FallbackPolicy = 默认拒：所有端点未显式 [AllowAnonymous] 的一律要求登录。
// 默认放行是「漏一个控制器就裸奔」，默认拒绝是「漏一个就 401」——后者坏了当场看得见。
builder.Services.Configure<IdentityOptions>(builder.Configuration.GetSection(IdentityOptions.SectionName));
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "aw.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        // API 不重定向到登录页：未登录的 XHR 应拿到 401，前端据此跳转。
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddCors(options => options.AddPolicy("spa", policy => policy
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

// 启动即迁移，并种子默认供应商
app.InitializeDatabase();
await app.Services.SeedDefaultProviderAsync();
await app.Services.SeedWorkflowAsync();
await app.Services.SeedDemoIdentityAsync();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("spa");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<AgentHub>("/hubs/agent");
app.MapHub<WorkflowHub>("/hubs/workflow");

app.Run();

/// <summary>
/// 顶层语句生成的 Program 默认是 internal，<c>WebApplicationFactory&lt;Program&gt;</c> 需要能找到它。
/// 只加这一个公开声明，不承载任何代码。
/// </summary>
public partial class Program { }
