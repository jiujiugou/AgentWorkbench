# AgentWorkbench.MockLlm

确定性、OpenAI 兼容的 **Mock 模型服务**（单文件 `Program.cs`），供测试/验收以「模型输入」驱动流程。
**永不调用真实模型** —— 它把「模型行为」变成可复现的输入，让 LLM 相关断言可以自动判定。

## 功能

| 端点 | 说明 |
| --- | --- |
| `POST /v1/chat/completions` | OpenAI 兼容补全：按场景返回**文本**或原生 **`tool_calls`**（支持多个）；支持 `stream=true` 流式 |
| `GET /v1/models` | 模型列表（供应商探测用） |
| `GET /health` | 健康检查 |
| `POST /control/scenario?name=`（或 JSON body） | 切换场景，未知场景 400 并回显可选值 |
| `POST /control/fixture` | 加载回放 fixture 并切到 `replay`（见下） |
| `POST /control/reset` | 重置调用计数与回放轮次游标（fixture 保留） |
| `GET /control/state` | `{ scenario, calls, turn, fixtureTurns }` |
| `GET /control/stats` | `{ scenario, calls }` |
| `GET /control/last-request` | 最近一次请求的紧凑摘要（DTO round-trip 断言用） |

**场景**：

| 场景 | 行为 | 用途 |
| --- | --- | --- |
| `normal` | 正常返回文本 | 正例绿 |
| `reject-tools` | 请求含 `tools` 时返回 400 `unsupported_parameter` | Chat 工具降级重试路径 |
| `harness-tool` / `harness-loop` | `calculator` 单次调用 / `get_current_time` 死循环 | 工具环与上限 |
| `replay` | 按轮次原样回放 fixture 的第 N 条 | τ²-bench 适配器 / 需要确定性多轮脚本的场景 |

> 历史垂直流程遗留的 `extract`/`agent` 请求上下文与 `badref`/`loop`/`illegal`/`nonjson` 场景，
> 已随 Review/ReviewAdvisor 一并删除（见 `docs/adr/0002-remove-vertical-modules.md`）。

## 回放 fixture（`replay`）

**为什么需要**：仓库内没有任何真实模型 API key。要让「模型」完全确定，唯一的办法是**预先写好它每一轮说什么**。

**格式**（两种都接受；`POST /control/fixture` 的 body 直接就是它）：

JSONL —— 一行一轮，空行与 `#` 注释被忽略：

```jsonl
# task 73 golden action
{"tool_calls": [{"id": "call_golden", "name": "return_delivered_order_items",
                 "arguments": {"order_id": "#W5272531", "item_ids": ["7228247242"],
                               "payment_method_id": "credit_card_6824399"}}]}
{"content": "###STOP###"}
```

或对象形式：

```json
{"turns": [
  {"tool_calls": [{"id": "c1", "name": "read_order", "arguments": {"order_id": "#A"}},
                  {"id": "c2", "name": "read_user", "arguments": {"user_id": "u1"}}]},
  {"content": "done"}
]}
```

**规则**：

- 每条 `tool_calls[].arguments` 写对象或字符串都可以；服务端统一输出为 **JSON 字符串**（OpenAI 协议要求）。
- `id` 省略时自动生成 `call_mock_<turn>_<i>`——**需要与 `ToolMessage` 配对时请显式写 id**。
- 一轮里 `content` 与 `tool_calls` **可以同时存在**：本服务忠实地「照 fixture 说什么就回什么」，
  **归一化（二选一）是调用方的责任**。τ²-bench 的适配器就在这里做归一化。
- **fixture 耗尽 → HTTP 500 `fixture_exhausted`**，绝不静默绕回第 1 轮。理由：静默绕回会让
  「模型多要了一轮」这种真实缺陷伪装成通过。
- 切到 `replay` 但尚未加载 fixture → 400（拒绝进入半坏状态）。

## 接口

无 C# 契约接口，对外即上表 HTTP 端点。可作为「模型供应商」被启动，供服务指向 `Provider:BaseUrl`。

## 框架提供 vs 自研

| 机制 | 来源 |
| --- | --- |
| `WebApplication` / Minimal API / `Results.Json` | ASP.NET Core（框架） |
| OpenAI Chat Completions 响应体（`choices` / `tool_calls` / `usage` / SSE 分块） | 自研（按 OpenAI 协议手工构造，无 SDK 依赖） |
| 场景切换、调用计数、fixture 解析与回放、控制面 | 自研 |

> 项目 csproj **无任何 NuGet 依赖**（仅 `Microsoft.NET.Sdk.Web`），是最纯粹的测试替身。

## 验收

```bash
# 起服务
dotnet tests/AgentWorkbench.MockLlm/bin/Debug/net10.0/AgentWorkbench.MockLlm.dll --urls http://localhost:5099
# 回放能力探针（21 条断言）
python tests/benchmark/tau2/probes/mockllm_replay_probe.py
# 全新实例负控（无 fixture 切 replay 必须被拒）
python tests/benchmark/tau2/probes/mockllm_replay_probe.py http://localhost:5098 --fresh-only
```

## 依赖

无（不引用任何 AgentWorkbench 模块）。
