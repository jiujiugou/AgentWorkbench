# AgentWorkbench.ModelInvocation

τ²-bench 适配面：**单步模型调用服务**。给 `messages[] + tools[]`，返回**一条** assistant 消息（文本或 `tool_calls`）；**不执行工具、不循环、不落库**。

工具执行权与判分权都在 τ² 的 Python orchestrator 侧——本服务只充当「模型的嘴」，把 .NET 侧的端点解析 / 客户端复用 / 超时重试闸门 / 用量计量暴露成一个 HTTP 端点。

## 为什么在 tests/ 下（而不是 src/）

原 V9 计划（`tests/benchmark/tau2/TASK_RESULT.md` §V9、`probes/webapi_entry_report.md`）设计为 `src/AgentWorkbench.ModelInvocation` + Webapi Controller。**该方案已修正**，原因：

| 维度 | 原方案（src） | 现方案（tests/） |
| --- | --- | --- |
| 架构测试同步 | 必须改 4 处（`TestArchitecture.AssemblyNames` / `LayeringTests` 正则 / `ModuleCycleTests.expected` / slnx） | **0 处**——架构测试的 `AssemblyNames` 是硬编码 src 清单，看不到本目录 |
| 产品面污染 | Webapi 长出评测专用 `POST /api/model/invoke` | Webapi 零改动 |
| src 消费者 | 假设中的「Conversation/Workflow/Harness 共用」 | 实际零消费者——那是假设性收益 |

先例：`tests/AgentWorkbench.BenchmarkHost`（同理由进 tests/）、`tests/AgentWorkbench.MockLlm`（tests 下的模型服务）。

**代价（如实记录）**：本目录项目不在架构测试保护下，可能悄悄长出坏依赖无人察觉——靠 csproj 注释与本文档自律。将来若产品真需要单步调用出口：`git mv` 到 `src/` + 补 4 处架构测试名单即可，引用集合（Providers / ModelRuntime / Persistence）全在 src，迁移成本约一小时。

## 端点

| 端点 | 说明 |
| --- | --- |
| `POST /api/model/invoke` | 单步调用（契约见 `IModelInvoker.cs`） |
| `GET /health` | 健康检查 |

### 请求

```json
{
  "messages": [
    { "role": "user", "text": "帮我算 (1+2)*3" },
    { "role": "assistant", "toolCalls": [{ "id": "call_1", "name": "calculator", "arguments": "{\"expression\":\"(1+2)*3\"}" }] },
    { "role": "tool", "toolCallId": "call_1", "text": "9" }
  ],
  "tools": [
    { "name": "calculator", "description": "计算表达式", "parameters": { "type": "object", "properties": { "expression": { "type": "string" } } } }
  ],
  "model": null
}
```

### 响应（200）

```json
{
  "message": {
    "role": "assistant",
    "text": null,
    "toolCalls": [{ "id": "call_2", "name": "calculator", "arguments": "{\"expression\":\"4*5\"}" }],
    "toolCallId": null
  },
  "model": "Qwen/Qwen3-8B",
  "usage": { "requests": 1, "inputTokens": 213, "outputTokens": 37 }
}
```

错误码：`400 invalid_request`（JSON 非法 / messages 为空）、`503 model_unavailable`（模型未配置，或模型未回 assistant 消息）。

`usage` 是**近似增量**（进程累计计量器的前后差值，并发下含他人增量），用途是评测侧粗略记账，不做计费、不做门禁。

## 启动与配置

```bash
dotnet run --project tests/AgentWorkbench.ModelInvocation
```

> **启动方式注意**：存储路径经 `StoragePaths.Resolve` 相对 **ContentRoot** 解析，而 ASP.NET 的 ContentRoot 默认 = 进程**当前工作目录**。
> `dotnet run --project` 时 ContentRoot = 项目目录（DB 落 `tests/AgentWorkbench.ModelInvocation/data/`）；
> **直接 `dotnet bin/.../AgentWorkbench.ModelInvocation.dll` 时 ContentRoot = cwd**（DB 会落到 cwd/data/——冒烟测试曾因此把仓库根目录的旧 DB 迁移升了级）。
> 要 DB 位置完全可控，显式传 `Storage__DbPath`。

模型配置两种接法（env 用 `__` 分层，V10 探针已证生效）：

1. **直配（推荐，评测隔离）**——默认库为空库、无激活供应商，自动降级到配置节：
   ```bash
   Provider__BaseUrl=https://api.siliconflow.cn/v1 \
   Provider__ApiKey=sk-xxx \
   Provider__Model=Qwen/Qwen3-8B \
   dotnet run --project tests/AgentWorkbench.ModelInvocation
   ```
2. **复用数据库激活供应商**：
   ```bash
   Storage__DbPath=<已迁移的库路径> dotnet run --project tests/AgentWorkbench.ModelInvocation
   ```

端口默认 `http://localhost:5000`，用 `ASPNETCORE_URLS` 覆盖。存储路径相对 ContentRoot 解析（`StoragePaths.Resolve`），与工作目录无关；启动时执行迁移（FluentMigrator 幂等，与 Webapi 同时迁移安全），不种子供应商。

## 复用的底座能力（不重写）

| 机制 | 来源 |
| --- | --- |
| 端点解析 / 客户端缓存 | `Providers.IChatClientFactory`（数据库激活供应商 → 配置回退） |
| 超时 / 重试 / 并发闸门 / 用量计量 | `ModelRuntime.IModelRuntime.Wrap` |
| 迁移 / 仓储 | `Persistence`（只读使用） |
| 工具声明（无委托） | `AIFunctionFactory.CreateDeclaration` —— 纯 schema，执行权不在本服务 |

## 与相邻项目的关系

- **`tests/AgentWorkbench.BenchmarkHost`**：OpenAI 兼容的 `/v1/chat/completions`（τ² 的 LLM 入口，即 τ² 配置里的 model endpoint）。本项目是 τ² 的 **agent 回合入口**。两者当前是两个进程/两个端口；若 τ² 侧希望单进程，可把 `MapPost("/api/model/invoke")` 合并进 BenchmarkHost 的 `Program.cs`（库代码原样搬，5 分钟）。
- **`src/AgentWorkbench.Webapi` 的 `POST /api/harness/run`**：产品面，run 级（内部含 AgentLoop 多轮循环、工具执行、RAG、记忆、落库）。与本服务不共用代码路径。

## 未付账单（接 τ² 推进计划）

- V8：MockLlm fixture 回放重写（请求 DTO 缺 `tool_calls` 等，见 `tests/benchmark/tau2/TASK_RESULT.md`）——没有它，全仓库无 API key 时整条链起不来；本服务可与 MockLlm 端点对接做契约冒烟。
- 本服务尚无自动化测试（契约层建议后续补：协议 DTO 往返 + 用 MockLlm 端点做一次端到端 invoke）。
