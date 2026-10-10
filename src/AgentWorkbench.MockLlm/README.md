# AgentWorkbench.MockLlm

确定性、OpenAI 兼容的 **Mock 模型服务**（单文件 `Program.cs`），供验收/评测脚本驱动 P3/P4 与 H（Harness）流程。
**永不调用真实模型** —— 它把「模型行为」变成可复现的输入，让 LLM 相关断言可以自动判定。

## 功能

| 端点 | 说明 |
| --- | --- |
| `POST /v1/chat/completions` | OpenAI 兼容补全：按请求上下文分类后，返回**文本**或原生 **`tool_calls`**；支持 `stream=true` 流式 |
| `GET /v1/models` | 模型列表（供应商探测用） |
| `GET /health` | 健康检查 + 当前 fixtures 目录 |
| `POST /control/scenario?name=`（或 JSON body） | 切换场景，未知场景 400 并回显可选值 |
| `POST /control/reset` | 重置调用计数 |
| `POST /control/delay?ms=` | 仅对抽取上下文注入人工延时（用于观察「run 仍在执行」的中间态） |
| `GET /control/stats` | `{ scenario, calls, delayMs, callsByContext }` —— 用于断言「调用了几次、调了哪类」 |

**请求分类**（`Classify`，依据 system+user 文本）：`extract`（含「抽取要素」）/ `agent`（含「待审查问题」）/ `other`。
`extract` 场景被强制固定，不受 `/control/scenario` 影响。

**场景**（fixtures 目录默认 `tests/fixtures/llm/*.json`，缺失时回退内置默认）：

| 场景 | 行为 | 对应负控 |
| --- | --- | --- |
| `normal` | 正常返回带引用结论（`agent-final-dynamic`） | 正例绿 |
| `badref` | 引用不存在的 `seq=999999` / `__NOT_IN_DOCUMENT__` | 引用后置校验必须降级为 insufficient |
| `loop` | 持续调用白名单工具 `locate_evidence` | 必须到 `maxSteps` 终止 |
| `illegal` | 调用白名单外工具 `drop_table` | 必须被拒绝并留痕 |
| `nonjson` | 返回非 JSON 文本 | 解析失败必须降级 |
| `reject-tools` | 请求含 `tools` 时返回 400 `unsupported_parameter` | Chat 工具降级重试路径 |
| `harness-tool` / `harness-loop` | `calculator` 单次调用 / `get_current_time` 死循环 | Harness 工具环与上限 |
| `extract` | 抽取上下文固定返回 `{}` | 规则抽取不受 LLM 影响 |

## 接口

无 C# 契约接口，对外即上表 HTTP 端点。被 `scripts/acceptance-p3.ps1`、`scripts/eval-p3.ps1`、
`scripts/acceptance-p4.ps1`、`scripts/acceptance-harness.ps1` 等作为「模型供应商」启动。

## 框架提供 vs 自研

| 机制 | 来源 |
| --- | --- |
| `WebApplication` / Minimal API / `Results.Json` | ASP.NET Core（框架） |
| OpenAI Chat Completions 响应体（`choices` / `tool_calls` / `usage` / SSE 分块） | 自研（按 OpenAI 协议手工构造，无 SDK 依赖） |
| 场景库、请求分类、调用计数、控制面 | 自研 |

> 项目 csproj **无任何 NuGet 依赖**（仅 `Microsoft.NET.Sdk.Web`），是最纯粹的测试替身。

## 依赖

无（不引用任何 AgentWorkbench 模块）。
