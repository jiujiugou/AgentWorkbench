# AgentWorkbench.Agent

对话 Agent 运行时模块：把「模型网关 → 对话/结构化输出」收敛成两个契约，屏蔽具体供应商，
是流式对话、RAG 问答、结构化输出与工作流步骤的共同底座。

## 功能

| 文件 | 说明 |
| --- | --- |
| `AgentServiceCollectionExtensions.cs` | 模块自注册入口 `AddNitroAgents`：注册 `IChatService` / `IStructuredChatService` / `IChatClientProvider` |
| `ChatService.cs` | 流式对话：`history + userMessage → IAsyncEnumerable<string>`；支持覆盖系统提示词（RAG 注入上下文）；模型不支持 tools 返回 4xx 时**自动降级为无工具重试一次** |
| `StructuredChatService.cs` | 强类型结构化输出：`GetJsonAsync<T>`，首次解析失败后**追加修复提示 + temperature=0 重试一次** |
| `IChatClientProvider.cs` | 按当前激活供应商/模型解析出 `IChatClient`（含默认实现），供需要原生函数调用的模块复用同一网关（当前无消费者，预留） |
| `IAgentToolProvider.cs` | 工具扩展点：实现它即可向对话 Agent 注入函数工具，调用方零改动 |

## 接口

| 接口 | 成员 | 实现 | 主要消费者 |
| --- | --- | --- | --- |
| `IChatService` | `IsConfiguredAsync`、`StreamAsync` | `ChatService` | Webapi `AgentHub`（流式对话）、Knowledge 的 RAG 问答 |
| `IStructuredChatService` | `IsConfiguredAsync`、`GetJsonAsync<T>` | `StructuredChatService` | 需要强类型 JSON 的调用方（当前预留） |
| `IChatClientProvider` | `GetClientAsync` | `ChatClientProvider` | 需要原生 function calling 的模块（当前预留，无消费者） |
| `IAgentToolProvider` | `GetTools()` | 各模块（AgentHarness `BuiltinTools`、未来的业务工具） | `ChatService` 构造时聚合注入 |
| `ChatTurn`（Domain，输入模型） | `Role` / `Content` | — | `StreamAsync` 的历史入参 |

## 框架提供 vs 自研

| 机制 | 来源 | 位置/说明 |
| --- | --- | --- |
| `ChatClientAgent`、`RunStreamingAsync`、`AgentResponseUpdate` | **MAF**（`Microsoft.Agents.AI`） | `ChatService.RunStreamAsync` |
| `ChatClientAgentRunOptions`、`ChatOptions`、`ChatMessage`/`ChatRole` | **MAF / MEA** | 消息与运行参数装配 |
| `IChatClient.GetResponseAsync<T>`（结构化输出） | **MEA**（`Microsoft.Extensions.AI`） | `StructuredChatService` |
| `AITool` / `AIFunctionFactory`（工具 Schema 生成、函数环执行） | **MEA** | `IAgentToolProvider` 的产物类型 |
| `ChatTurn` → `ChatMessage` 消息装配、系统提示词覆盖 | 自研 | `ChatService.BuildMessages` |
| tools 4xx → 无工具重试的**降级策略** | 自研（策略层） | `ChatService.StreamAsync` 的 `while` 重试 + `degraded` 标记 |
| JSON 解析失败 → 追加修复提示重试 | 自研（策略层） | `StructuredChatService.GetJsonAsync` |
| 模型端点解析（多供应商/多模型、激活切换） | 自研 | `AgentWorkbench.Providers` 的 `IChatClientFactory` |

> 一句话：**框架给的是「怎么调模型、怎么装配消息、怎么执行函数」，本模块给的是「降级、重试、契约统一」。**

## 依赖

Domain、Providers、Shared。
