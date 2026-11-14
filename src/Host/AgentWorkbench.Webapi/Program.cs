using AgentWorkbench.Runtime.Loop;
using AgentWorkbench.Runtime.Harness;
using AgentWorkbench.Runtime.Context;
using AgentWorkbench.Documents;
using AgentWorkbench.Knowledge.Local;
using AgentWorkbench.Runtime.Session;
using AgentWorkbench.Memory.Persistence;
using AgentWorkbench.Runtime.Model;
using AgentWorkbench.Persistence;
using AgentWorkbench.Runtime.Model;
using AgentWorkbench.Runtime.Tools;
using AgentWorkbench.Tools.Builtin;
using AgentWorkbench.Tools.Mcp;
using AgentWorkbench.Webapi.Actions;
using AgentWorkbench.Webapi.Auth;
using AgentWorkbench.Webapi.Hubs;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
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
builder.Services.AddNitroContext(builder.Configuration);
builder.Services.AddNitroToolExecution(builder.Configuration);
builder.Services.AddNitroBuiltinTools();
// 外部工具来源：MCP server。默认关闭（Mcp:Enabled=false 时启动期不连任何 server）。
builder.Services.AddNitroMcp(builder.Configuration);
builder.Services.AddNitroKnowledge(builder.Configuration);
builder.Services.AddNitroDocuments(builder.Configuration);
builder.Services.AddNitroMemory(builder.Configuration);
builder.Services.AddNitroMemoryPersistence(builder.Configuration);
builder.Services.AddNitroObservability();
builder.Services.AddNitroAgentLoop(builder.Configuration);
builder.Services.AddNitroAgentHarness(builder.Configuration);
// M1 受控动作链：四道校验 → 挂起审批 → 执行 → 审计。换域只改 Actions:ActionSet。
builder.Services.AddNitroActions(builder.Configuration);

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
builder.Services.Configure<DemoIdentityOptions>(builder.Configuration.GetSection(DemoIdentityOptions.SectionName));

// ── ASP.NET Core Identity：用户、密码哈希、锁定、角色全部交给框架 ──
// 存储走 EF Core（IdentityDbContext<AppUser>），建表仍由 FluentMigrator M029 负责。
builder.Services.AddHttpContextAccessor();
builder.Services
    .AddIdentityCore<AppUser>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.User.RequireUniqueEmail = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddRoles<IdentityRole>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>()
    .AddDefaultTokenProviders()
    .AddEntityFrameworkStores<AppDbContext>();

builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
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

// SPA 深链接（/approvals、/actions/{id}）刷新时不能 404：那是前端路由，不是后端接口。
// 不配这一段的话，`localhost:8080` 能打开首页，但只要刷新任何一页就变成空白——
// 而「首页好看、一刷新就炸」是最容易被当成偶发问题放过去的一种。
//
// /api 与 /hubs 刻意不参与回退：它们的 404 必须如实返回。若被 index.html 盖掉，
// 调试时看到的是「接口 200 + 一页 HTML」，那是最难查的形态。
//
// **必须 AllowAnonymous**：全局 FallbackPolicy 是「默认拒」（见下方 Identity 配置），
// 不加这句的话，未登录的人连登录页都打不开——`GET /` 直接 401，而前端路由守卫
// 根本没机会跑起来。SPA 的静态入口本来就是匿名的：权限由 API 那侧判。
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var webRoot = app.Environment.WebRootPath;
    var indexPath = string.IsNullOrEmpty(webRoot) ? null : Path.Combine(webRoot, "index.html");
    if (indexPath is null || !File.Exists(indexPath))
    {
        // 明确说「没构建过」，不要回一页空白让人去翻浏览器控制台。
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("前端未构建：先执行 cd web && npm run build");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(indexPath);
}).AllowAnonymous();

app.Run();

/// <summary>
/// 顶层语句生成的 Program 默认是 internal，<c>WebApplicationFactory&lt;Program&gt;</c> 需要能找到它。
/// 只加这一个公开声明，不承载任何代码。
/// </summary>
public partial class Program { }
