# AgentWorkbench.ToolExecution

工具调用模块（功能域 2）：工具注册扩展点 + 白名单目录 + 「函数调用环」执行 + 通用内置工具。

与数据域解耦：**知识库工具住在 `Knowledge.Local/Tools`，文档工具住在 `Documents/Tools`** ——
工具平台不反向依赖它们的仓储（各模块自己实现 `IAgentToolProvider`）。

## 功能

| 文件 | 说明 |
| --- | --- |
| `IAgentToolProvider.cs` | 工具扩展点：**一个工具一个类**，返回 MEA `AIFunctionFactory` 生成的 `AITool`，便于按工具名定位代码 |
| `ToolCatalog.cs` | `IToolCatalog`：汇总所有 provider 的工具，按 `IToolPolicy` 白名单过滤 + 按名去重；延迟解析 provider 避免构造期 DI 环 |
| `ToolExecutionPipeline.cs` | `IToolExecutionPipeline.Create(innerClient, limits)` → 装配 `FunctionInvokingChatClient`（`MaximumIterationsPerRequest` = 预算）并挂 `FunctionInvoker` 钩子采集 `ToolCallTrace`（工具名/参数/结果/失败原因，超长截断）。工具抛异常时记 `Error`（异常类型 + 消息）后原样抛回 MEA |
| `ToolExecutionOptions.cs` | 配置节 `Harness:ToolExecution`：`MaxResultChars=400` |
| `Builtin/GetCurrentTimeTool.cs` | 内置只读工具 `get_current_time` |
| `Builtin/CalculatorTool.cs` + `Builtin/SafeCalculator.cs` | 内置只读工具 `calculator`（自研安全四则运算解析器，不用 `eval`、无外部依赖） |
| `ToolExecutionServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroToolExecution`（含 2 个通用内置工具） |

## 接口

| 接口 | 成员 | 实现 | 消费者 |
| --- | --- | --- | --- |
| `IAgentToolProvider` | `GetTools()` | ToolExecution（2 个内置）/ Knowledge.Local（`search_knowledge`）/ Documents（`list_documents`） | 由 `ToolCatalog` 聚合 |
| `IToolCatalog` | `GetTools()`、`GetToolNames()` | `ToolCatalog` | Conversation `ChatService`、AgentHarness |
| `IToolExecutionPipeline` | `Create(client, limits)` | `ToolExecutionPipeline` | AgentHarness |
| `IToolPolicy`（`AgentWorkbench.Safety`） | `Whitelist`、`IsAllowed`、`Allowed` | `ToolPolicy` | `ToolCatalog` |

## 框架提供 vs 自研

| 机制 | 来源 | 位置/说明 |
| --- | --- | --- |
| `FunctionInvokingChatClient` + `MaximumIterationsPerRequest` | **MEA** | **函数调用环本体**（自动执行函数、回填结果、循环到上限） |
| `FunctionInvoker` 回调 | **MEA** | 借它挂轨迹采集 |
| `AIFunctionFactory.Create`（工具 Schema） | **MEA** | 各 `IAgentToolProvider` 的产物类型 |
| 白名单裁剪、按名去重、轨迹采集与截断 | 自研（策略层） | `ToolCatalog` / `ToolExecutionPipeline` |
| 安全四则运算求值 | 自研 | `Builtin/SafeCalculator` |

> 边界：MEA 负责「怎么执行函数、怎么回填」，本模块负责「哪些工具可见、一次执行留什么痕迹」。

## 依赖

Safety、Shared。
