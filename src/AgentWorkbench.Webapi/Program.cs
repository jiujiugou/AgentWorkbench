using AgentWorkbench.Agent;
using AgentWorkbench.AgentHarness;
using AgentWorkbench.Documents;
using AgentWorkbench.Knowledge;
using AgentWorkbench.Persistence;
using AgentWorkbench.Providers;
using AgentWorkbench.Workflow;
using AgentWorkbench.Webapi.Hubs;
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
builder.Services.AddNitroAgents(builder.Configuration);
builder.Services.AddNitroKnowledge(builder.Configuration);
builder.Services.AddNitroDocuments(builder.Configuration);
builder.Services.AddNitroAgentHarness(builder.Configuration);
builder.Services.AddNitroWorkflow(builder.Configuration);
// 用 SignalR 覆盖 Workflow 模块的默认空进度通知
builder.Services.AddSingleton<IWorkflowProgressNotifier, SignalRWorkflowProgressNotifier>();

builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("spa");

app.MapControllers();
app.MapHub<AgentHub>("/hubs/agent");
app.MapHub<WorkflowHub>("/hubs/workflow");

app.Run();
