# AgentWorkbench.AgentHarness

Agent Harness（运行时横切轴）模块：在 MAF 之上补齐 **loop / context / memory / tools / trace** 五个横切关注点的策略与持久化。
让 Agent「不只是能跑，还能被限制、被观测、被复现」。

## 功能

| 文件 | 说明 |
| --- | --- |
| `AgentHarnessService.cs` | Harness 主服务：建 Agent（工具环 + 会话 + 历史）→ 可选 RAG 注入 → 带超时运行 → 轨迹落库 → 返回结果；失败也落 `failed` run |
| `HarnessOptions.cs` | 配置节 `Harness`：`Loop.MaxIterations=6` / `Loop.TimeoutSeconds=30` / `Context.Enabled`+`RagTopK=5` / `Memory.SessionEnabled` / `Tools.Whitelist`（4 个内置工具） |
| `HarnessModels.cs` | 契约模型：`HarnessCapabilities`、`HarnessRunRequest`、`HarnessStep`、`HarnessRunResult`、`HarnessModelNotConfiguredException` |
| `Tools/BuiltinTools.cs` | 4 个内置**只读**工具（`get_current_time` / `calculator` / `search_knowledge` / `list_documents`），仓储通过 `IServiceProvider` 惰性解析避免 DI 循环 |
| `Tools/SafeCalculator.cs` | 自研安全四则运算解析器（不用 `eval`，无外部依赖） |
| `HarnessServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroAgentHarness` |
| Webapi `HarnessController` | HTTP 面：`api/harness/health` · `POST run` · `GET runs/{id}` |

## 接口

| 接口 | 成员 | 实现 | 说明 |
| --- | --- | --- | --- |
| `IAgentHarness` | `GetCapabilitiesAsync`、`RunAsync`、`GetRunAsync` | `AgentHarnessService` | 唯一对外契约；Webapi / Workflow `AgentStepExecutor` 消费 |
| `IAgentToolProvider` | `GetTools()` | `BuiltinTools` | 复用 Agent 模块的工具扩展点 |
| `IAgentRunRepository`（Persistence） | `InsertRunAsync` / `InsertStepsAsync` / `GetRunAsync` / `GetStepsAsync` | — | 轨迹持久化（M014 `agent_runs` / M015 `agent_steps`） |
| `IKnowledgeEngine`（Knowledge.Abstractions） | `RetrieveAsync` | — | 上下文注入 + `search_knowledge` 工具 |

## 框架提供 vs 自研

| 机制 | 来源 | 位置/说明 |
| --- | --- | --- |
| `ChatClientAgent` + `ChatClientAgentOptions` | **MAF** | `AgentHarnessService.BuildAgent` |
| `InMemoryChatHistoryProvider` + `AgentSession` | **MAF** | 会话/历史容器（`ChatHistoryProvider` 扩展点） |
| `FunctionInvokingChatClient` + `MaximumIterationsPerRequest` | **MEA** | **工具环**本体（自动执行函数、回填结果、循环到上限） |
| `FunctionInvoker` 回调（中间件扩展点） | **MEA** | 本模块借它挂轨迹采集 |
| `AIFunctionFactory.Create` | **MEA** | `BuiltinTools` 工具 Schema |
| 工具**白名单强制**（不在白名单不注册、不可调用） | 自研（策略） | `RunAsync` 的 `.Where(Whitelist.Contains)` + `AllowedToolNames` |
| run 级**超时**（linked CTS + `CancelAfter`） | 自研（策略） | `RunAsync`；超时抛 `TimeoutException` 并落 `failed` |
| **RAG 上下文注入**（检索 → 拼「【知识库检索片段】」→ 前置到 user 消息） | 自研 | `RunAsync` + `BuildContext` |
| **轨迹采集**（迭代号/工具名/参数/结果，400 字符截断） | 自研（借 `FunctionInvoker` 钩子） | `FunctionInvoker` 委托 → `HarnessStep` |
| **轨迹持久化/回放** | 自研 | `IAgentRunRepository` + `GetRunAsync` |
| 会话记忆（进程内 `ConcurrentDictionary`，**重启即丢**） | 自研 | `_sessions`；MAF 只提供会话对象，存储策略自定 |
| 能力位 `HarnessCapabilities` | 自研 | `GetCapabilitiesAsync` |

> 边界：MAF 负责「怎么循环、怎么执行工具」，Harness 负责「允许调什么、跑多久、上下文从哪来、跑完留下什么」。

## 依赖

Agent、Providers、Knowledge.Abstractions、Persistence、Domain、Shared。
