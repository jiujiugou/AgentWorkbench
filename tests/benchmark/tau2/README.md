# τ²-bench × AgentWorkbench 接入

把 [τ²-bench](https://github.com/sierra-research/tau2-bench) 接成 AgentWorkbench 的**外部独立裁判**。
它测的是「.NET 侧能不能作为一个合格的 agent，在真实业务数据 + 真实工具 + 客观判分下办成事」，
**不是** harness 的编排质量（工具执行权与判分权都在 τ² 的 Python orchestrator 手里）。

> 这是本仓库里**唯一**一个「出题人不是我们自己」的客观基准，专门治自建框架的**评测自循环**病
> （自己出题、自己作答、自己判分）。

---

## 一、它算什么、不算什么

| | 自建框架（`dotnet test`） | τ²-bench（本目录） |
|---|---|---|
| 判什么 | 机制正确性 / 不变量 | 端到端任务成功率 |
| 类型 | 判定型（绿/红 + 负控） | 度量型（reward + 趋势） |
| 跑在哪 | 每次 `dotnet test`，秒级 | 里程碑 / 夜间，分钟级 |
| 治的病 | 机制回归 | 评测自循环 |
| 判据来源 | **自建**（产品规格手写期望值） | **外部**（τ² 官方 golden DB 状态） |

**互补，不替代**：τ² 只能通过 `POST /api/model/invoke` 跟我们说话，看不见守卫 / 预算 / 审批 /
轨迹落库 / RAG。删掉自建框架 = 这些机制完全失去覆盖。

---

## 二、架构：四层数据流

```
┌──────────────────────────────────────────────────────────────────┐
│ τ² Orchestrator (Python, D:\Code\tau2-bench)          ← 外部基准   │
│   • 持有工具执行权：自己在真实 DB 上 get_response(tool_call)       │
│   • 持有判分权：最终 DB 状态 vs golden 状态                       │
└───────────────────────────┬──────────────────────────────────────┘
                            │ 每轮只喂「最新一条」消息
                            ▼
┌──────────────────────────────────────────────────────────────────┐
│ adapter/harness_agent.py  (Python, 本仓库唯一手写胶水层)   ← 要写   │
│   tau2 消息 ⇄ 协议 DTO；不执行工具、不循环、不落库                 │
└───────────────────────────┬──────────────────────────────────────┘
                            │ HTTP POST /api/model/invoke
                            ▼
┌──────────────────────────────────────────────────────────────────┐
│ AgentWorkbench.ModelInvocation (.NET, tests/)            ← 已有   │
│   收 messages[]+tools[] → 回**一条** assistant 消息               │
│   复用 Providers + ModelRuntime，不造第二套调用语义                │
└───────────────────────────┬──────────────────────────────────────┘
                            │ OpenAI 兼容 /chat/completions
                            ▼
┌──────────────────────────────────────────────────────────────────┐
│ AgentWorkbench.MockLlm (.NET, tests/)                    ← 已有   │
│   replay 场景：逐轮原样回放 fixture（确定性，不联网）              │
└──────────────────────────────────────────────────────────────────┘
```

**确定性来源**：模型侧 = MockLlm 的 replay fixture；用户侧 = `ScriptedUser`（脚本化）。
全仓无 API key，因此整条链路完全不调用任何真实模型。

---

## 三、目录

```
tests/benchmark/tau2/
├── README.md                    ← 本文件
├── setup_env.ps1                ← 准备 tau2 环境（uv + 固定 commit + venv + websockets 补丁）
├── run_smoke.py                 ← ★ 端到端冒烟运行器（自启两个 .NET 服务 + 正负控判分）
├── adapter/                     ← ★ Python 适配器（唯一手写胶水层）
│   ├── __init__.py              ←   register_all()：运行时注册，不改 tau2 源码
│   ├── harness_agent.py         ←   HalfDuplexAgent 子类 + 消息转换
│   └── scripted_user.py         ←   确定性 user
├── fixtures/                    ← 模型回放脚本
│   ├── task73_golden.jsonl      ←   正控：golden 动作
│   └── task73_bad_order.jsonl   ←   负控：错误 order_id
├── probes/                      ← 验证期留下的探针（历史证据，非主链）
├── FINDINGS.md                  ← V1–V11 验证发现
├── TASKS.md                     ← 原始任务清单与边界
└── TASK_RESULT.md               ← 失败项与后续行动
```

`.run/` 是运行器自启服务时的日志目录（已 gitignore）。

---

## 四、快速开始

```bash
# 0) 一次性：准备 tau2 环境（clone 固定 commit + venv + 依赖补丁）
powershell -ExecutionPolicy Bypass -File tests\benchmark\tau2\setup_env.ps1

# 1) 构建两个 .NET 服务
dotnet build tests/AgentWorkbench.MockLlm
dotnet build tests/AgentWorkbench.ModelInvocation

# 2) 跑端到端冒烟（自己拉起两个服务，跑完自动关闭）
D:\Code\tau2-bench\.venv\Scripts\python.exe tests/benchmark/tau2/run_smoke.py
```

可选参数：`--task 73`、`--no-start`（假定服务已在跑）、`--keep-services`（跑完不关服务）。

**期望结果**（见下方「已证事实」）：

```
结果：PASS —— 正控 reward=1.0 且负控 reward=0.0，全链跑通
```

---

## 五、已证事实（2026-10-14 实测，可复现）

跑 `run_smoke.py` 得到 **17 项断言全绿**，其中最关键的是：

| 断言 | 实测 |
|---|---|
| 正控 `reward == 1.0` | ✅ `db_match = True` |
| 负控 `reward == 0.0` | ✅ 工具返回 `Error: Order not found`，`db_match = False` |
| 轨迹中 `assistant.tool_calls` 与 `ToolMessage` 成对 | ✅ 各 1 条 |
| 终止于 `AGENT_STOP` | ✅（靠 `###STOP###`） |
| MockLlm 恰好消费 2 轮回放 | ✅ `{"calls":2,"turn":2}`（证明无重试错位） |
| registry 运行时注册成功 | ✅ `agentworkbench` + `scripted_user` |

**正控单独绿没有意义**——只有正控 1.0 **且**负控 0.0 同时成立，才说明「链路通了**而且**判分有牙」。

---

## 六、三个契约

### 6.1 协议 DTO（`harness_agent.py` ⇄ `IModelInvoker.cs`）

请求 `POST /api/model/invoke`：

```json
{
  "messages": [
    {"role": "system", "text": "...", "toolCalls": null, "toolCallId": null},
    {"role": "assistant", "text": null, "toolCalls": [{"id":"c1","name":"f","arguments":"{\"a\":1}"}], "toolCallId": null},
    {"role": "tool", "text": "...", "toolCalls": null, "toolCallId": "c1"}
  ],
  "tools": [{"name": "f", "description": "...", "parameters": {"type": "object"}}],
  "model": null
}
```

响应：

```json
{"message": {"role": "assistant", "text": "...", "toolCalls": null, "toolCallId": null},
 "model": "mock", "usage": {"requests": 1, "inputTokens": 0, "outputTokens": 0}}
```

`toolCalls[].arguments` 是 **JSON 字符串**（原样透传），不是对象——适配器双向转换。

### 6.2 fixture 格式（MockLlm replay）

一行 = 一轮模型输出。支持 `{"content": ...}`、`{"tool_calls":[...]}` 或两者兼有：

```jsonl
{"tool_calls":[{"id":"call_golden_1","name":"return_delivered_order_items","arguments":{"order_id":"#W5272531","item_ids":["..."]}}]}
{"content":"已办理退货。###STOP###"}
```

`arguments` 写成 JSON 对象即可（MockLlm 会重序列化成字符串）。也接受 `{"turns":[...]}`。
**耗尽 fixture → HTTP 500 `fixture_exhausted`（fail-loud，绝不静默回绕）。**

### 6.3 MockLlm 控制面

| 端点 | 作用 |
|---|---|
| `POST /control/fixture` | 装载 fixture 并自动切到 replay（轮次归零） |
| `POST /control/scenario?name=` | 切场景（`normal` / `harness-tool` / `harness-loop` / `reject-tools` / `replay`） |
| `POST /control/reset` | 复位调用计数与轮次游标（保留 fixture） |
| `GET  /control/state` | `{scenario, calls, turn, fixtureTurns}` |

---

## 七、适配器要处理的六个接缝（都是从源码核出来的）

1. **system prompt 得自己拼**——orchestrator 只喂 user/tool 消息，`domain_policy` 是构造参数。
   复用 tau2 自己的 `SYSTEM_PROMPT` / `AGENT_INSTRUCTION` 常量，保证「模型输出相同 ⇒ 行为相同」。
2. **`MultiToolMessage` 要展开**——一条 assistant 带多个 tool_call 时，结果被包成一条多消息。
3. **文本与 tool_calls 不可兼得**——归一化：有 tool_calls 就丢文本、`content=None`。
4. **空消息 = 直接 0 分**——τ² 的 `validate()` 会判 `AGENT_ERROR`，适配器提前 fail-loud。
5. **`###STOP###` 是终止铁律**——不显式 stop 就跑满 `max_steps` → 0 分。
6. **`ToolCall.arguments` 是 dict**——协议给的是 JSON 字符串，要 `json.loads`。

---

## 八、诚实边界（说清楚不测什么）

- **不测 harness 编排**：工具执行与判分都在 τ² 侧，我们只是「一张合格的嘴」。守卫 / 预算 /
  审批 / 轨迹落库 / RAG 全部不在覆盖范围（那是自建框架的活）。
- **用户侧是脚本化的**：`ScriptedUser` 无法驱动真实的 114 个任务，只够打通链路。
  真实评测需换回 `user_simulator`（要 key）或把 user 侧也接到 MockLlm。
- **不碰 LLM-judge**：带 `nl_assertions` 的 40 个任务需要 LLM 打分，撞《测试标准》§1.1 红线，
  白名单外不用。当前只跑 74 个可纯确定性判分的任务（task 73 是其中之一）。
- **不改基准本体**：`D:\Code\tau2-bench` 只读，适配器通过**运行时注册**接入。

---

## 九、红线

1. **不为接 τ² 重写评测基建**——只写适配器，复用 MockLlm / ModelInvocation。
2. **不把 τ² 塞进 `dotnet test`**——它要起两个服务 + Python venv + 真实 DB，慢；
   塞进快速反馈循环会拖垮开发体验。
3. **不魔改 tau2 源码**——扩展点走 registry，版本用 commit 锁死。
