# τ²-bench × AgentWorkbench 验证发现记录

---

## V1 — 端到端能不能拿到 reward 1.0？

- **结论**：PASS
- **证据**：
  - 正例运行 `probe_scripted_run.py`：
    ```
    Reward: 1.0
    Reward basis: [<RewardType.DB: 'DB'>, <RewardType.NL_ASSERTION: 'NL_ASSERTION'>]
    DB check: db_match=True db_reward=1.0
    Termination reason: TerminationReason.AGENT_STOP
    Steps: 4
    Messages in trajectory: 5
    ```
  - 轨迹关键片段：
    ```
    [0] AssistantMessage: "Hi! How can I help you today?"
    [1] UserMessage: "Thank you for processing my return. I appreciate your help."
    [2] AssistantMessage(tool_calls=[return_delivered_order_items with correct args])
    [3] ToolMessage (responding to assistant): {... "return_items": ["2698416822", "3320557165", "7228247242", "8098621301"], "return_payment_method_id": "credit_card_6824399" ...}
    [4] AssistantMessage: "###STOP###"
    ```
  - 负控运行（把 order_id 改为 `#WRONG_ORDER_ID`）：
    ```
    Reward: 0.0
    DB check: db_match=False db_reward=0.0
    ToolMessage content: "Error: Order not found"
    ```
- **反例**：负控正确变红（reward 0.0），说明验证有效。
- **未解释的现象**：
  - `reward_basis` 包含 `NL_ASSERTION` 但 `nl_assertions` 为 None，`EvaluationType.ENV` 仍给出 1.0 —— 说明 ENV 评分器只检查 DB/ENV_ASSERTION，忽略 NL_ASSERTION 基础。
  - 初始消息是 `DEFAULT_FIRST_AGENT_MESSAGE` ("Hi! How can I help you today?")，而非 task 的 user_scenario —— 符合 orchestrator 无 initial_state 时的初始化逻辑。
- **产出物**：
  - `tests/benchmark/tau2/probes/probe_scripted_run.py`
  - `tests/benchmark/tau2/FINDINGS.md` (本文件)

---

## V2 — `strict_replay` 会不会炸？

- **结论**：FAIL（会炸）
- **证据**：
  - `strict_replay=True` 抛出 `ValueError`，错误信息显示：
    ```
    Recorded ToolMessage: "Error: Non-delivered order cannot be returned"
    Replayed ToolMessage: 成功的返回结果 (完整订单 JSON)
    ```
  - `strict_replay=False` 正常通过，reward=1.0，并打印警告：
    ```
    Replayed tool call 'return_delivered_order_items' returned different content than the recorded ToolMessage; continuing because strict=False.
    ```
  - 根因：首次模拟运行已修改数据库（订单状态变为 "return requested"），评分器重放时环境状态不同，导致工具执行结果不一致。
  - **关键启示**：`strict_replay` 要求环境是幂等/可重置的，或者必须在评分前恢复环境快照。当前 τ²-bench 的 Environment 无自动快照/回滚机制。
- **反例**：`strict_replay=False` 能跑通，但会丢失"轨迹与真实执行一致"的保证。
- **未解释的现象**：
  - 评分器内部是通过 `environment.set_state()` + 重放 tool_calls 来重建状态的，但它没有在重放前重置环境数据库。
  - 这意味着：同一个 environment 实例跑多次 simulation，第 2 次评分时 strict_replay 必挂（除非任务不改 DB）。
- **产出物**：
  - `tests/benchmark/tau2/probes/probe_scripted_run.py` (含 test_strict_replay 函数)

---

## V3 — 半双工 retail 的 user 到底该怎么造？

- **结论**：PASS（已验证可用最小实现）
- **证据**：
  - `DummyUser.generate_next_message` 抛 `NotImplementedError`，orchestrator 的 `_validate_mode_compatibility` 检查要求 user 有 `generate_next_message` 方法（orchestrator.py:472-475）。
  - 最小实现需两个方法：
    1. `get_init_state(message_history=None) -> UserState`：返回 `UserState(system_messages=[], messages=[])`
    2. `generate_next_message(message, state) -> (UserMessage, UserState)`：根据收到的消息返回回复，最后一条必须含 `###STOP###`
  - 验证：V1 的 `ScriptedUser` 类已成功跑通，轨迹显示 user 消息被正确追加，发 STOP 后 `termination_reason == USER_STOP`（若由 user 发 STOP）或 `AGENT_STOP`（若由 agent 发 STOP）。
  - Orchestrator 认 `###STOP###`：`UserSimulator.is_stop()` / `HalfDuplexAgent.is_stop()` 均检查 content 中是否含此标记。
- **反例**：用 `DummyUser` 跑 half-duplex retail 会在 `_validate_mode_compatibility` 抛 `ValueError: User DummyUser must have 'generate_next_message' method.`（实际上是 NotImplementedError 在调用时抛出）。
- **未解释的现象**：无。
- **产出物**：
  - `tests/benchmark/tau2/probes/probe_scripted_run.py` 中的 `ScriptedUser` 类（可复用）

---

## V4 — 初始化路径到底喂给 agent 什么？

- **结论**：PASS（源码确认，V1 实跑印证）
- **证据**：
  - **无 `initial_state` 时**（如 task 73）：
    - Orchestrator 创建 `DEFAULT_FIRST_AGENT_MESSAGE` ("Hi! How can I help you today?")
    - `agent.get_init_state(message_history=[DEFAULT_FIRST_AGENT_MESSAGE])`
    - `user.get_init_state(message_history=[])`
    - 首轮：AGENT -> USER 发送该默认消息
    - V1 轨迹印证：`[0] AssistantMessage: "Hi! How can I help you today?"`
  - **有 `initial_state` 时**（orchestrator.py:490-525）：
    - `message_history = deepcopy(initial_state.message_history)` + 加时间戳
    - 根据最后一条消息类型决定 `from_role`/`to_role`，并把过滤后的 history 分别传给 agent/user 的 `get_init_state`
    - 例如：最后是 AssistantMessage(text) -> `from_role=AGENT, to_role=USER`，agent 拿 `message_history[:-1]`，user 拿 `message_history[:-1]`
- **反例**：无。
- **未解释的现象**：
  - Task 的 `user_scenario` 不会直接喂给 agent/user，仅供 LLM user simulator 参考。Scripted/确定性 user 需自行编码场景。
- **产出物**：
  - 无额外产出，V1 探针已覆盖。

---

## V5 — 哪些 retail 任务能不用 LLM 判分？

- **结论**：PASS（已验证短路逻辑）
- **证据**：
  - 统计：114 个 retail base 任务中，**74 个** `nl_assertions` 为空（None/[]），**40 个**非空。
  - 空 nl 任务白名单（74 个）：
    `0, 1, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 17, 18, 20, 22, 23, 25, 26, 27, 30, 31, 32, 33, 34, 35, 41, 42, 48, 49, 50, 51, 52, 53, 55, 56, 57, 58, 61, 64, 65, 66, 69, 71, 72, 73, 74, 75, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 90, 91, 92, 93, 94, 96, 97, 98, 99, 100, 101, 102, 112`
  - 实测 task 73（空 nl，但 reward_basis 含 NL_ASSERTION）用 `EvaluationType.ALL` 跑：
    - **无 LLM 调用**（网络无流量，日志无 `nl_assertions_eval`）
    - `reward=1.0`，`reward_breakdown` 含 `NL_ASSERTION: 1.0`
    - 源码 `evaluator_nl_assertions.py:38-44`：`if not nl_assertions: return RewardInfo(reward=1.0, ...)` 直接短路
  - 实测 task 2（有 nl_assertions）用 `EvaluationType.ALL` 跑：
    - **触发 1 次 LLM 调用**（`model=gpt-4.1-2025-04-14, call_name=nl_assertions_eval`）
    - 因无 API Key 报错，但已证明会走 LLM
- **反例**：task 2 等非空 nl 任务确实触发 LLM，证明短路非巧合。
- **未解释的现象**：
  - 空 nl 任务的 `reward_basis` 仍包含 `NL_ASSERTION`，但评分器按“是否有实际断言”短路，不按 reward_basis 判断。
- **产出物**：
  - 白名单见上；`probe_scripted_run.py` 可直接跑这些任务做确定性冒烟。

---

## V6 — 一条 assistant 消息里多个 tool_calls 会怎样？

- **结论**：PASS（行为明确）
- **证据**：
  - 发送含 2 个 tool_call 的 AssistantMessage：
    ```
    ToolCall(id=235f2d8f..., name=get_order_details, ...)
    ToolCall(id=a5ef8269..., name=get_user_details, ...)
    ```
  - 环境**顺序执行**两个工具，产出 2 个 ToolMessage（轨迹索引 3、4）
  - 回传给 agent 时被**包装为 MultiToolMessage**，内含两个 ToolMessage，id 与请求一一对应
  - Agent 下一轮 `generate_next_message` 接收的是 `MultiToolMessage` 类型
- **反例**：无。
- **未解释的现象**：
  - 两个 ToolMessage 的 `timestamp` 相同（批量执行同一步），`turn_idx` 连续（3, 4）
  - `MultiToolMessage` 本身无 `turn_idx`，内部 ToolMessage 有
- **产出物**：
  - 无额外产出，探针逻辑已验证。

---

## V7 — 终止条件边界：不显式结束会怎样？

- **结论**：PASS（铁律确认）
- **证据**：
  - Agent 和 User 都不发 STOP，`max_steps=5` 跑满：
    - `termination_reason = TerminationReason.MAX_STEPS`
    - `reward = 0.0`
    - `reward_info.info = {'note': 'Simulation terminated prematurely. Termination reason: max_steps'}`
  - 源码 `evaluator.py:119-129`：非 `AGENT_STOP`/`USER_STOP` 直接返回 `reward=0.0`，**不再检查任何评分维度**
- **反例**：无。
- **未解释的现象**：
  - 即使轨迹里已经完成了正确的工具调用，只要没显式 STOP 也得 0 分。
  - 这是设计使然：防止 agent/user 无限对话、强制显式终止。
- **产出物**：
  - 无额外产出。

---

## V8 — MockLlm 现状盘点与回放方案

- **结论**：
- **证据**：
- **反例**：
- **未解释的现象**：
- **产出物**：

---

## V9 — Webapi 有没有可复用的「单步模型调用」入口？

- **结论**：
- **证据**：
- **反例**：
- **未解释的现象**：
- **产出物**：

---

## V10 — 确定性跑怎么隔离 provider / DB？

- **结论**：
- **证据**：
- **反例**：
- **未解释的现象**：
- **产出物**：

---

## V11 — `setup_env.ps1` 在干净环境下能否幂等跑通？

- **结论**：
- **证据**：
- **反例**：
- **未解释的现象**：
- **产出物**：