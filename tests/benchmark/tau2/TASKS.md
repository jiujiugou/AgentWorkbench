# τ²-bench × AgentWorkbench 验证任务清单

> 这份文档是**交给其他模型执行的验证/调研任务书**。任务是「回答问题 + 给出可复核的证据」，
> **不是生产代码开发任务**。允许写一次性探针脚本，禁止改动生产代码。

---

## 0. 先读这一节：任务性质与边界

### 允许做的

- 在 `tests/benchmark/tau2/probes/` 下写**一次性**验证脚本（Python / PowerShell 均可）。
- 在 `tests/benchmark/tau2/` 下写报告、dump、fixture、README。
- **只读**地阅读 `D:\Code\tau2-bench`（τ²-bench 源码）、本仓库 `src/**`、`tests/**`。
- 跑命令、起本地进程、抓日志。

### 禁止做的

- **不修改 `D:\Code\tau2-bench` 的任何文件**（它是外部基准，只读引用）。
- **不修改本仓库生产代码**：`src/**`、`web/**`、`tests/AgentWorkbench.*/**`、`AgentWorkbench.slnx`。
- **不动 `tests/benchmark/bi/`**（另一条基准线，正在被使用）。
- 不提交 `bin/ obj/ node_modules/ dist/`。
- 不新增 NuGet / pip 包到项目依赖（探针脚本可以临时用 τ²-bench venv 里已有的包）。
- 不为了让结论好看而修改基准数据、任务 JSON 或评分器。

### 每个任务的统一交付格式

所有结论写进**同一个文件** `tests/benchmark/tau2/FINDINGS.md`，按任务号分节，每节包含：

```
## V<n> <任务名>
- 结论：PASS / FAIL / 部分成立（一句话）
- 证据：命令 + 关键输出片段（原样粘贴，不要复述）
- 反例：负控怎么做的、结果如何
- 未解释的现象：（如果有）
- 产出物：脚本路径 / 数据文件路径
```

**判定标准**：证据必须是"跑出来的"，不能是"我读了源码所以应该"。凡是能从源码推断但没实跑的，明确标注「未实跑」。

---

## 1. 背景（最小必要）

τ²-bench 是 Sierra Research 的真实业务数据 Agent 基准：半双工对话 + 真实工具 + 真实数据库 + 可判分。
本仓库要用它来测 AgentWorkbench 作为 agent 的对错。

```
τ²-bench (Python, D:\Code\tau2-bench)
  Orchestrator ── agent = HarnessAgent 适配器 ──HTTP──> AgentWorkbench Webapi
       │                                                      │
       │                                              AgentHarness → 模型
       └── 工具执行在这里（真实改 DB）+ 评分也在这里
```

### 已定的架构决策（不要再论证，也不要推翻）

**工具执行权在 τ²-bench 的 `Orchestrator.step()` 手里，不在 .NET 侧。**

依据：`src/tau2/orchestrator/orchestrator.py` 的 `step()` 中，当 `from_role=AGENT, to_role=ENV` 时，
它自己调用 `environment.get_response(tool_call)`，并把 `ToolMessage` / `MultiToolMessage` 追加进轨迹。

由此推导出的三条约束：

1. **不写 Python 桥接服务**（不需要）。
2. **不写 .NET 远程工具执行模块**（会让同一个真实 DB 被改两次）。
3. **不做轨迹合成**（轨迹是 orchestrator 天然产出的）。

适配器的形状因此固定为：**每次被调用只返回一条消息，要么是文本，要么是 tool_calls，不能两者兼有、不能为空。**

---

## 2. 环境事实（不要再重新踩一遍）

| 项 | 值 |
|---|---|
| uv | `C:\Users\jiuji\.local\bin\uv.exe`（0.12.23）；同目录有 `uvw.exe` / `uvx.exe` |
| 调用方式 | `$env:UV_BIN="C:\Users\jiuji\.local\bin\uv.exe"; cd D:\Code\tau2-bench; & $env:UV_BIN run python <绝对路径脚本>` |
| τ²-bench 路径 | `D:\Code\tau2-bench` |
| 锁定 commit | `4ce7c0397c1eb65c9bbe59aeacfe1ca44a1cd699`（pyproject version 1.0.1，**无 tag**，只能锁 commit） |
| 已知坑 | 只 `uv sync` 会 `import tau2` 失败：`simulation.py` 无条件 import `LiveConfig`，而 `websockets` 不在核心依赖。必须补 `uv pip install websockets`。**不要装 `--extra voice`**，太重 |
| Shell | **PowerShell 5.1**，**没有 `sed` / `head`**。取行用 `Get-Content ... \| Select-Object -Skip N -First M` |
| 编辑文件 | 一律用 `apply_patch`，不要用 shell 写文件 |
| 编码 | 中文文件在 PS5.1 控制台会显示乱码（GBK/UTF-8），**内容本身有效**；新建文件统一 UTF-8 |
| 冷启动 | `uv run` 首次 8–20s（litellm import） |
| 网络 | `HTTP_PROXY` / `HTTPS_PROXY` 为空，uv 联网正常 |

---

## 3. 已确认的事实（**不要重新验证**，直接引用）

| 事实 | 依据 |
|---|---|
| uv 可用、τ²-bench 可 clone 并锁到 commit | 实跑 |
| `uv sync` + 补 `websockets` 后 `import tau2` 通过，域列表含 `retail` | 实跑 |
| retail base split 共 **114** 个任务，加载成功 | 实跑（`dump_tasks.py`） |
| retail 有 **16** 个 assistant 工具，**没有 user 工具** → `get_user_tools()` 抛 `ValueError`，`get_user_db_hash()` 两侧都是 `None` | 已 dump |
| retail 域 policy 长 6699 字符 | 已 dump |
| `reward_basis` 分布：`DB+NL_ASSERTION`=112，`DB`=2 | 已 dump |
| `n_nl_assertions` 分布：0→74、1→25、2→10、3→4、4→1。**74 个任务可不用 LLM 判分** | 已 dump |
| golden action 的 requestor **全部是 assistant**（112 个任务有 golden actions） | 已 dump |
| **task 73** 是干净冒烟目标：1 个 golden action、无 nl_assertions、无 communicate_info、无 env_assertions、无 initial_state | 已 dump |

task 73 的关键数据（写 fixture 直接用）：

```json
{
  "id": "73",
  "tool": "return_delivered_order_items",
  "arguments": {
    "order_id": "#W5272531",
    "item_ids": ["7228247242", "2698416822", "8098621301", "3320557165"],
    "payment_method_id": "credit_card_6824399"
  },
  "user": { "name": "Fatima Wilson", "email": "fatima.wilson5721@example.com",
            "reason": "want to return everything except the coffee machine" }
}
```

---

## 4. 需要验证的问题（任务清单）

依赖关系：V1 是根，V2/V3/V4/V6/V7 都挂在它上面（可以一个人连着做）。
V8 / V9 独立，可并行。V5 独立。V10 依赖 V9。V11 独立。

---

### V1 — 端到端能不能拿到 reward 1.0？（最高优先，没这条后面全白搭）

| | |
|---|---|
| **要回答** | 用一个**脚本化 agent + 脚本化 user**（完全不调 LLM），对 retail task 73 跑一次 `run_simulation`，reward 是否等于 1.0？ |
| **为什么重要** | 这一次跑通同时验证：orchestrator 真的执行工具、轨迹正确、`set_state` 重放成立、评分器给出 1.0、终止条件被识别。 |
| **怎么做** | 写 `probes/probe_scripted_run.py`：<br>1. `registry.get_env_constructor("retail")()` 造 env；<br>2. 从 `registry.get_tasks_loader("retail")("base")` 取 task 73；<br>3. 写一个最小 `HalfDuplexAgent` 子类，`generate_next_message` 返回固定脚本（第一轮直接发 `return_delivered_order_items` 的 tool_call，收到 tool 结果后发一句结束文本）；<br>4. 写一个最小 `HalfDuplexUser` 子类，固定回一句 + 最后发 `###STOP###`；<br>5. `Orchestrator(...)` + `run_simulation(orchestrator, evaluation_type=EvaluationType.ENV)`；<br>6. 打印 `reward_info`、`termination_reason`、完整轨迹。 |
| **判定** | **正例**：`reward == 1.0`，`termination_reason ∈ {USER_STOP, AGENT_STOP}`，轨迹里能看到 `AssistantMessage(tool_calls)` + `ToolMessage` 成对出现。<br>**负控（必做）**：把脚本里的 `order_id` 改成不存在的值 → `reward == 0.0`。负控不变红说明验证无效。 |
| **产出** | `probes/probe_scripted_run.py` + `FINDINGS.md` 的 V1 节（贴 reward 与轨迹片段） |
| **规模** | M |

---

### V2 — `strict_replay` 会不会炸？

| | |
|---|---|
| **要回答** | `EnvironmentEvaluator` 用 `strict=True` 重放轨迹时，会把**记录的 ToolMessage.content** 与**重放执行的真实输出**做 JSON 全等比较。真实跑一次会不会抛异常？ |
| **为什么重要** | 这是最容易失败、且失败信息最隐蔽的点。如果它挂，V1 就过不去，必须提前知道原因。 |
| **怎么做** | 在 V1 的脚本里显式跑两遍：`strict_replay=True` 与 `strict_replay=False`，对比。若 True 抛异常，抓完整堆栈 + 差异字段。 |
| **判定** | `strict_replay=True` 不抛异常 → PASS。若抛，给出：哪个工具、哪个字段、记录值 vs 重放值。 |
| **产出** | `FINDINGS.md` 的 V2 节 + 差异样本 |
| **依赖 / 规模** | V1 / S |

---

### V3 — 半双工 retail 的 user 到底该怎么造？

| | |
|---|---|
| **要回答** | `DummyUser.generate_next_message` 抛 `NotImplementedError`，retail 又不支持 solo 模式。那么确定性 user 的最小实现是什么？orchestrator 认不认手写的 `###STOP###`？ |
| **为什么重要** | 没有可用的 user，半双工对话根本起不来。这是当前最大的未决点。 |
| **怎么做** | 在 V1 里实现最小 `HalfDuplexUser` 子类（只实现 `get_init_state` + `generate_next_message`），最后一条消息内容含 `###STOP###`。 |
| **判定** | user 消息被正确追加进轨迹；发 STOP 后 `termination_reason == USER_STOP`；`Orchestrator._validate_mode_compatibility` 通过。 |
| **产出** | 可复用的 `ScriptedUser` 实现（放 `probes/`）+ FINDINGS 记录其最小必要接口 |
| **依赖 / 规模** | 可与 V1 合并 / S |

---

### V4 — 初始化路径到底喂给 agent 什么？

| | |
|---|---|
| **要回答** | 无 `message_history` 时，`agent.get_init_state()` 收到的是不是 `[AssistantMessage(role="assistant", content="Hi! How can I help you today?")]`？task 带 `initial_state` 时，收到的 history 又是什么？ |
| **为什么重要** | 适配器要按这个决定"第一轮该看到什么"，猜错就会多一轮/少一轮。 |
| **怎么做** | 在假 agent 的 `get_init_state(message_history)` 里 `print(json.dumps(...))`，跑 task 73（无 initial_state）和一个带 initial_state 的任务，对照。 |
| **判定** | 打印结果与源码推断一致/不一致，明确写出。 |
| **产出** | FINDINGS 的 V4 节（贴原始打印） |
| **依赖 / 规模** | V1 / S |

---

### V5 — 哪些 retail 任务能不用 LLM 判分？

| | |
|---|---|
| **要回答** | 确认「`nl_assertions` 为空时 `NLAssertionsEvaluator` 会短路返回 1.0、不调 LLM」。据此列出可确定性跑的任务白名单。 |
| **为什么重要** | 决定冒烟集能选哪些任务；选错会引入 LLM 依赖，破坏确定性。 |
| **怎么做** | 1. 复用已导出的 `_retail_tasks.json` 统计 `nl_assertions` 为空的任务 id 列表；<br>2. 对其中一个空 nl 任务用 `EvaluationType.ALL` 实跑，确认无 LLM 调用（可用 monkeypatch 把 LLM 调用函数替换成抛异常来强验证）；<br>3. 再挑一个 nl 非空的任务，确认它**确实**会走 LLM（证明前一步的短路不是巧合）。 |
| **判定** | 空 nl 任务在 ALL 下不触发 LLM；非空任务触发。两条都成立才算 PASS。 |
| **产出** | 白名单任务 id 列表 + FINDINGS 节 |
| **规模** | S |

---

### V6 — 一条 assistant 消息里多个 tool_calls 会怎样？

| | |
|---|---|
| **要回答** | 一条消息含 2 个 tool_call 时：执行顺序、轨迹里 `ToolMessage` 的排列、agent 下一轮收到的是 `MultiToolMessage` 吗？`ToolCall.id` 与 `ToolMessage.id` 怎么配对？ |
| **为什么重要** | 直接影响适配器的状态机和 MockLlm 回放 fixture 的写法（id 必须能对上）。 |
| **怎么做** | 脚本化一条含 2 个 tool call 的 assistant 消息（可挑两个读类工具，避免改 DB），跑完打印轨迹。 |
| **判定** | 2 个 `ToolMessage` 按序出现、id 配对正确、下一轮输入类型确认。 |
| **产出** | FINDINGS 的 V6 节 + 轨迹片段 |
| **依赖 / 规模** | V1 / S |

---

### V7 — 终止条件边界：不显式结束会怎样？

| | |
|---|---|
| **要回答** | agent 和 user 都不发 stop 时，跑到 `max_steps` 之后 `reward` 是不是 0？`termination_reason=MAX_STEPS` 时 evaluator 是否直接返回 0？ |
| **为什么重要** | 确认"必须显式结束否则 0 分"这条铁律，否则会误判适配器失败。 |
| **怎么做** | 造一个永不 stop 的脚本化 agent，`max_steps=5` 跑一次，观察 `termination_reason` 与 `reward_info.reward` / `info.note`。 |
| **判定** | 明确写出 `MAX_STEPS` 下的 reward 与 note 原文。 |
| **依赖 / 规模** | V1 / S |

---

### V8 — MockLlm 现状盘点与回放方案（**只出方案，不改代码**）

| | |
|---|---|
| **要回答** | 现有 MockLlm 的请求/响应模型到底缺什么？能不能表达 tool_calls？能否按"第 N 轮"回放固定脚本？最小改动方案是什么？ |
| **为什么重要** | 用户明确要求"MockLlm 可能不准确，需要重新做"。改动前必须先有准确的缺口清单，否则会改歪。 |
| **怎么做** | 1. 读 `tests/AgentWorkbench.MockLlm/Program.cs` 与 `README.md`，列出请求/响应 DTO 的字段；<br>2. 起 MockLlm（`dotnet tests\AgentWorkbench.MockLlm\bin\Debug\net10.0\AgentWorkbench.MockLlm.dll --urls http://localhost:5099`），用一段 Python/curl 探针 POST 一个**含 `tool_calls` 和 `role=tool` 历史**的请求，记录它的解析行为 / 报错 / 实际返回；<br>3. 给出"回放所需最小改动方案"与 fixture 格式提案（含轮次索引规则）。 |
| **判定** | 产出可执行的缺口清单（逐字段：现状 / 需要 / 影响）+ 方案提案。**不能只写"需要支持 tool_calls"这种空话。** |
| **产出** | `tests/benchmark/tau2/probes/mockllm_report.md`（独立文件）+ FINDINGS 摘要 |
| **规模** | M |
| **提醒** | 不要改 `Program.cs`。这是调研任务。 |

---

### V9 — Webapi 有没有可复用的「单步模型调用」入口？

| | |
|---|---|
| **要回答** | Webapi 现有端点里，有没有能"给 messages + tools，返回一条 assistant 消息（文本或 tool_calls）"的？`AgentHarness.RunAsync` 能否复用，还是必须新增？ |
| **为什么重要** | 适配器要指向 Webapi，必须先知道 .NET 侧的最小新增面。这决定了后面所有实现工作的形状。 |
| **怎么做** | 只读地读 `src/AgentWorkbench.Webapi/Controllers/`、`src/AgentWorkbench.AgentHarness/`、`src/AgentWorkbench.ModelRuntime/`、`src/AgentWorkbench.ToolExecution/`，回答：<br>1. 现有端点清单（路径 + 入参 + 出参）；<br>2. `AgentHarness` 每次 run 是否只接受单个 user 文本、历史靠 session 存；<br>3. 若要实现"单步"，最小新增面是什么（新增类？新增端点？是否要新模块？）；<br>4. 如果新增模块，**必须同步的文件清单**（`AgentWorkbench.slnx`、`Webapi/Program.cs`、Webapi csproj 引用、以及 3 处架构测试硬编码名单：`TestArchitecture.cs` 的 `AssemblyNames`、`LayeringTests.cs` 的 infrastructure/modules 列表、`ModuleCycleTests.cs` 的 `expected[]`）。 |
| **判定** | 产出决策建议（扩展 AgentHarness vs 新增模块）+ 逐文件清单 + 依据。**不写生产代码。** |
| **产出** | `tests/benchmark/tau2/probes/webapi_entry_report.md` |
| **规模** | M |

---

### V10 — 确定性跑怎么隔离 provider / DB？

| | |
|---|---|
| **要回答** | 仓库默认 `data/agentworkbench.db` 里有**激活的 SiliconFlow provider（含明文 key）**，确定性跑绝不能走它。`Provider__BaseUrl` / `Provider__ApiKey` / `Provider__Model` / `Storage__DbPath` 这些 env 覆盖是否真的生效？ |
| **为什么重要** | 不隔离就会真的打外部模型：不确定、要花钱、还可能泄露 key 到日志。 |
| **怎么做** | 1. 起一个极简 HTTP stub（随便什么语言，只要能记录收到的请求并返回一个假的 OpenAI 兼容响应）；<br>2. `Provider__BaseUrl` 指向 stub，`Storage__DbPath` 指到临时目录；<br>3. 触发一次 Webapi 的模型调用；<br>4. 确认请求打到 stub，且**没有**外部网络调用（可对比日志/耗时）。 |
| **判定** | stub 收到请求 = PASS；同时确认默认 DB 没被写入。 |
| **产出** | `probes/` 下的 stub 脚本 + FINDINGS V10 节 |
| **依赖 / 规模** | V9 / M |

---

### V11 — `setup_env.ps1` 在干净环境下能否幂等跑通？

| | |
|---|---|
| **要回答** | 删除 venv（或换个临时目录）后，`tests/benchmark/tau2/setup_env.ps1` 是否能从零跑通？重复跑第二次是否仍然成功？ |
| **为什么重要** | 别人能不能复现这套环境，全看它。 |
| **怎么做** | 备份/改名现有 `.venv` 后跑两次脚本，记录退出码与耗时。**注意：改名前确认路径，别误删。** |
| **判定** | 两次退出码都是 0，且第二次不重复 clone/不报错。 |
| **产出** | FINDINGS V11 节（贴两次运行的尾部输出） |
| **规模** | S |

---

## 5. 任务完成后的统一验收命令

```powershell
# τ²-bench 侧
$env:UV_BIN="C:\Users\jiuji\.local\bin\uv.exe"
cd D:\Code\tau2-bench
& $env:UV_BIN run python D:\Code\AgentWorkbench\tests\benchmark\tau2\probes\probe_scripted_run.py

# 环境脚本
powershell -ExecutionPolicy Bypass -File D:\Code\AgentWorkbench\tests\benchmark\tau2\setup_env.ps1
```

> 本轮任务是验证/调研，**不需要**跑 `dotnet build` 或架构测试——因为不允许改生产代码。
> 如果某个任务意外需要改生产代码，停下来，先写进 FINDINGS 说明原因，不要直接改。

---

## 6. 已知风险（做任务时留意，不要当成结论）

| 风险 | 说明 | 相关任务 |
|---|---|---|
| `strict_replay` 全等比较 | 重放输出与记录输出只要差一个字段就 raise | V2 |
| 必须显式结束 | `termination_reason` 不是 AGENT_STOP/USER_STOP 时直接 0 分，且不给任何解释性 note | V7 |
| 半双工需要真实 user | retail 不支持 solo，`DummyUser` 不可用 | V3 |
| 空消息/混合消息 | 文本与 tool_calls 不能同时出现，也不能都为空，违反会被判 `AGENT_ERROR` → 0 分 | V1、V6 |
| provider 泄漏 | 默认 DB 里有激活的真实 provider | V10 |
| 任务选择 | 有 `nl_assertions` 的任务会引入 LLM 判分，破坏确定性 | V5 |

---

## 7. 已存在的文件（可直接用，不要重写）

| 路径 | 说明 |
|---|---|
| `tests/benchmark/tau2/setup_env.ps1` | 环境准备：定位 uv、clone 锁 commit、`uv sync`、补 `websockets`、冒烟 |
| `tests/benchmark/tau2/dump_domain.py` | 域结构只读导出（工具 schema、reward_basis 分布、任务摘要） |
| `tests/benchmark/tau2/dump_tasks.py` | 完整任务导出（含 golden actions 参数与 user_scenario） |
| `tests/benchmark/tau2/_retail_tasks.json` | 114 个 retail base 任务的完整 dump（**已 gitignore**） |
| `tests/benchmark/tau2/_retail_dump.json` | 域结构 dump（**已 gitignore**） |
| `tests/benchmark/tau2/.gitignore` | 忽略 `_*.json` / `__pycache__` |

---

## 8. 交付顺序建议

1. **V1 + V2 + V3 + V4**（一条线，同一个人做）→ 这是整个方向的生死线。
2. **V5 + V6 + V7**（可并行，都挂在 V1 的框架上）。
3. **V8 / V9**（独立调研，可立即并行开工，不依赖 V1）。
4. **V10**（依赖 V9）。
5. **V11**（随时可做）。

如果 V1 失败，**停止其余任务**，先把 V1 的失败原因写清楚——后面所有设计都建立在它之上。
