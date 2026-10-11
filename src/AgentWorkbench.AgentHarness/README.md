# AgentWorkbench.AgentHarness

Agent 运行时**编排门面**：一次 run 的入口。
把模型交互 / 工具调用 / 执行控制 / 上下文 / 记忆 / 安全 / 可观测各自独立的功能域模块装配成一条流水线，
**自身不实现任何一项能力**（迭代循环本体由 MAF 承担）。

## 一次 run 的流水线

```
ResolveAsync(端点)                                    Providers
  → IModelRuntime.Wrap(client)                        模型交互（超时/重试/并发/用量）
  → IToolExecutionPipeline.Create(client, 预算)        工具调用（工具环 + 轨迹采集）
      ↑ 预算 = IAgentLoop.MaxIterations               执行控制
  → ChatClientAgent（工具来自 IToolCatalog）           对话运行时 + 白名单目录（Safety）
  → IContextBuilder.Compose(输入, 检索命中)             上下文（UseKnowledge=true 时）
  → ISessionStore.GetOrCreateAsync(sessionId)          记忆
  → IAgentLoop.ExecuteAsync(...)                      执行控制（run 级超时）
  → IExecutionTraceStore.SaveAsync(轨迹)               可观测（成功/失败都落库）
```

## 功能

| 文件 | 说明 |
| --- | --- |
| `AgentHarnessService.cs` | 编排上面这条流水线；失败也落 `failed` run（保留已完成的工具步骤） |
| `HarnessModels.cs` | 契约模型：`HarnessCapabilities`、`HarnessRunRequest`、`HarnessStep`、`HarnessRunResult`、`HarnessModelNotConfiguredException` |
| `HarnessServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroAgentHarness`（只注册 `IAgentHarness`，能力全在别的模块） |
| Webapi `HarnessController` | HTTP 面：`api/harness/health` · `POST run` · `GET runs/{id}` |

## 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IAgentHarness` | `GetCapabilitiesAsync`、`RunAsync`、`GetRunAsync` | `AgentHarnessService` | Webapi `HarnessController`、Workflow `AgentStepExecutor` |

### 编排依赖的契约（能力都在别的模块）

| 契约 | 模块 | Harness 用它做什么 |
| --- | --- | --- |
| `IChatClientFactory` | Providers | 解析端点、取 `IChatClient` |
| `IModelRuntime` | ModelRuntime | 每个模型请求的超时/重试/并发/用量 |
| `IToolCatalog` | ToolExecution | 取白名单内的工具（授权策略来自 Safety） |
| `IToolExecutionPipeline` | ToolExecution | 装配工具环 + 采集 `ToolCallTrace` |
| `IAgentLoop` | AgentLoop | 迭代预算 + run 级超时 |
| `IContextBuilder` | Context | 检索片段 → user 文本 |
| `ISessionStore` | Memory | 按 sessionId 复用会话 |
| `IExecutionTraceStore` | Observability | 轨迹落库与回放 |
| `IKnowledgeEngine` | Knowledge.Abstractions | 检索（`UseKnowledge=true` 时） |

## 配置节归属

| 配置节 | 归属模块 | 内容 |
| --- | --- | --- |
| `Harness:ModelRuntime` | ModelRuntime | 单次请求超时 / 重试 / 退避 / 并发闸门 |
| `Harness:ToolExecution` | ToolExecution | 轨迹结果截断长度 |
| `Harness:Tools` | Safety | 工具白名单 |
| `Harness:Loop` | AgentLoop | `MaxIterations` / `TimeoutSeconds` |
| `Harness:Context` | Context | `Enabled` / `RagTopK` |
| `Harness:Memory` | Memory | `SessionEnabled` |

> 节名沿用原有 `Harness:*` 路径，`appsettings` 无需改写；配置的**解析与默认值**已随功能挪到各自模块。

## 框架提供 vs 自研

| 机制 | 来源 | 位置/说明 |
| --- | --- | --- |
| `ChatClientAgent` + `ChatClientAgentOptions` | **MAF** | `BuildAgent` |
| `InMemoryChatHistoryProvider` + `AgentSession` | **MAF** | 历史容器（会话的持有/复用策略在 `Memory`） |
| `FunctionInvokingChatClient`（工具环本体） | **MEA** | 由 `ToolExecution` 装配 |
| 装配顺序、失败也要留痕、能力位 | 自研（编排层） | `RunAsync` / `GetCapabilitiesAsync` |
| 工具白名单、超时、RAG 注入、轨迹、会话复用 | 自研（各自模块） | 见上表 |

> 边界：Harness 只做**装配与失败记录**。「允许调什么」在 Safety + ToolExecution，「跑多久/几次」在 AgentLoop，「上下文从哪来」在 Context，「留什么痕迹」在 Observability。

## 依赖

ModelRuntime、ToolExecution、AgentLoop、Context、Memory、Observability、Providers、Knowledge.Abstractions、Shared。
