# AgentWorkbench.MockLlm

确定性、OpenAI 兼容的 **Mock 模型服务**（单文件 `Program.cs`），供测试/验收以「模型输入」驱动流程。
**永不调用真实模型** —— 它把「模型行为」变成可复现的输入，让 LLM 相关断言可以自动判定。

## 功能

| 端点 | 说明 |
| --- | --- |
| `POST /v1/chat/completions` | OpenAI 兼容补全：按场景返回**文本**或原生 **`tool_calls`**；支持 `stream=true` 流式 |
| `GET /v1/models` | 模型列表（供应商探测用） |
| `GET /health` | 健康检查 |
| `POST /control/scenario?name=`（或 JSON body） | 切换场景，未知场景 400 并回显可选值 |
| `POST /control/reset` | 重置调用计数 |
| `GET /control/stats` | `{ scenario, calls }` —— 用于断言「调用了几次」 |

**场景**（内置，无外部 fixture 文件）：

| 场景 | 行为 | 用途 |
| --- | --- | --- |
| `normal` | 正常返回文本 | 正例绿 |
| `reject-tools` | 请求含 `tools` 时返回 400 `unsupported_parameter` | Chat 工具降级重试路径 |
| `harness-tool` / `harness-loop` | `calculator` 单次调用 / `get_current_time` 死循环 | 工具环与上限 |

> 历史垂直流程遗留的 `extract`/`agent` 请求上下文与 `badref`/`loop`/`illegal`/`nonjson` 场景，
> 已随 Review/ReviewAdvisor 一并删除（见 `docs/adr/0002-remove-vertical-modules.md`）。

## 接口

无 C# 契约接口，对外即上表 HTTP 端点。可作为「模型供应商」被启动，供服务指向 `Provider:BaseUrl`。

## 框架提供 vs 自研

| 机制 | 来源 |
| --- | --- |
| `WebApplication` / Minimal API / `Results.Json` | ASP.NET Core（框架） |
| OpenAI Chat Completions 响应体（`choices` / `tool_calls` / `usage` / SSE 分块） | 自研（按 OpenAI 协议手工构造，无 SDK 依赖） |
| 场景切换、调用计数、控制面 | 自研 |

> 项目 csproj **无任何 NuGet 依赖**（仅 `Microsoft.NET.Sdk.Web`），是最纯粹的测试替身。

## 依赖

无（不引用任何 AgentWorkbench 模块）。
